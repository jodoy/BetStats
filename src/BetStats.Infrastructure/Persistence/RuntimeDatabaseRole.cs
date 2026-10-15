using Npgsql;

namespace BetStats.Infrastructure.Persistence;

public static class RuntimeDatabaseRole
{
    // Explicit administrator operation, never called during host startup.
    // Passwords originate from operator environment/secrets, never arguments/logs.
    public static async Task ConfigureAsync(NpgsqlConnection connection, string adminPassword, string appPassword, CancellationToken token)
    {
        if (adminPassword.Length < 24 || appPassword.Length < 24 || adminPassword == appPassword) throw new ArgumentException("Distinct strong passwords required.");
        await using var tx = await connection.BeginTransactionAsync(token);
        async Task Execute(string sql) { await using var command = new NpgsqlCommand(sql, connection, tx); await command.ExecuteNonQueryAsync(token); }
        await Execute("DO $$ BEGIN IF NOT EXISTS(SELECT FROM pg_roles WHERE rolname='betstats_admin') THEN CREATE ROLE betstats_admin LOGIN; END IF; IF NOT EXISTS(SELECT FROM pg_roles WHERE rolname='betstats_app') THEN CREATE ROLE betstats_app LOGIN; END IF; END $$;");
        foreach (var (role, password) in new[] { ("betstats_admin", adminPassword), ("betstats_app", appPassword) })
        {
            await using var quote = new NpgsqlCommand("SELECT format('ALTER ROLE %I PASSWORD %L', @role, @password)", connection, tx);
            quote.Parameters.AddWithValue("role", role); quote.Parameters.AddWithValue("password", password);
            await Execute((string)(await quote.ExecuteScalarAsync(token))!);
        }
        await Execute("ALTER ROLE betstats_app NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT; ALTER ROLE betstats_admin NOSUPERUSER NOCREATEROLE NOREPLICATION NOBYPASSRLS CREATEDB;");
        // Deny unexpected memberships instead of silently preserving inherited power.
        await using (var memberships = new NpgsqlCommand("SELECT count(*) FROM pg_auth_members WHERE member=(SELECT oid FROM pg_roles WHERE rolname='betstats_app')", connection, tx))
            if ((long)(await memberships.ExecuteScalarAsync(token))! != 0) throw new InvalidOperationException("Runtime role must have no memberships.");
        var database = new NpgsqlCommandBuilder().QuoteIdentifier(connection.Database);
        await Execute($"ALTER DATABASE {database} OWNER TO betstats_admin; REVOKE CREATE,TEMPORARY ON DATABASE {database} FROM PUBLIC; GRANT CONNECT ON DATABASE {database} TO betstats_app;");
        await Execute("REVOKE CREATE ON SCHEMA public FROM PUBLIC;");
        // Fixed application schemas; no blanket rights on third-party/system schemas.
        foreach (var schema in new[] { "ingestion", "canonical", "governance", "provenance", "quality", "datasets", "coverage", "football", "evaluation", "pipeline" })
        {
            await using var exists = new NpgsqlCommand("SELECT EXISTS(SELECT FROM pg_namespace WHERE nspname=@name)", connection, tx); exists.Parameters.AddWithValue("name", schema);
            if (!(bool)(await exists.ExecuteScalarAsync(token))!) continue;
            var identifier = new NpgsqlCommandBuilder().QuoteIdentifier(schema);
            await Execute($"ALTER SCHEMA {identifier} OWNER TO betstats_admin; GRANT USAGE ON SCHEMA {identifier} TO betstats_app; GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA {identifier} TO betstats_app; GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA {identifier} TO betstats_app; ALTER DEFAULT PRIVILEGES FOR ROLE betstats_admin IN SCHEMA {identifier} GRANT SELECT,INSERT,UPDATE,DELETE ON TABLES TO betstats_app; ALTER DEFAULT PRIVILEGES FOR ROLE betstats_admin IN SCHEMA {identifier} GRANT USAGE,SELECT ON SEQUENCES TO betstats_app;");
            // Transfer object ownership, so migrations as betstats_admin can alter
            // existing tables/functions, while betstats_app cannot disable triggers.
            await using var objects = new NpgsqlCommand("SELECT 'ALTER ' || CASE c.relkind WHEN 'S' THEN 'SEQUENCE ' ELSE 'TABLE ' END || quote_ident(n.nspname)||'.'||quote_ident(c.relname)||' OWNER TO betstats_admin' FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@name AND c.relkind IN ('r','p') AND NOT EXISTS(SELECT FROM pg_depend d WHERE d.objid=c.oid AND d.deptype='a')", connection, tx);
            objects.Parameters.AddWithValue("name", schema); var statements = new List<string>();
            await using (var reader = await objects.ExecuteReaderAsync(token)) while (await reader.ReadAsync(token)) statements.Add(reader.GetString(0));
            foreach (var statement in statements) await Execute(statement);
            await using var functions = new NpgsqlCommand("SELECT 'ALTER FUNCTION '||p.oid::regprocedure::text||' OWNER TO betstats_admin' FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname=@name AND p.prokind='f'", connection, tx);
            functions.Parameters.AddWithValue("name", schema); statements.Clear();
            await using (var reader = await functions.ExecuteReaderAsync(token)) while (await reader.ReadAsync(token)) statements.Add(reader.GetString(0));
            foreach (var statement in statements) await Execute(statement);
        }
        await Execute("ALTER TABLE public.\"__EFMigrationsHistory\" OWNER TO betstats_admin; GRANT SELECT ON public.\"__EFMigrationsHistory\" TO betstats_app;");
        await tx.CommitAsync(token);
    }
}
