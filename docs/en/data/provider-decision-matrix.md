# BS-018 provider decision matrix

Reviewed 2026-10-13 using official documentation. No provider request, dataset
download, subscription purchase or permission message was made. These are candidate
assessments, not legal approval. No operator-specific grant or credential was supplied.

| Candidate | Permitted private use and authorization | Access / authentication / quotas / cost | Data and pre/post-match capability | Retention / attribution / decision |
| --- | --- | --- | --- | --- |
| API-Football / API-Sports | Projects are described, but rights-holder use/publication rights remain the user's responsibility; no blanket downstream license. Actual operator purposes remain Unknown. | Documented v3 HTTPS, `x-apisports-key`; free 100/day with season limits, Pro $19/month at review time; per-minute plan quota must be confirmed. | Competition/season coverage varies; fixtures include teams, status, kickoff, results; history depends on subscription; indicative update frequency is not a publication-time guarantee. | RAW retention, analysis/model use, display and attribution need purpose-specific review. **Technical boundary candidate only; live approval blocked.** |
| Football-Data.co.uk | Private spreadsheet analysis is described; commercial/data-training and automated access restrictions prevent assuming the required grant. | Country/season CSV, no key for public files; no numerical quota established; downloads advertised free. | Historical results/stats and periodically updated upcoming fixtures; no durable provider event UUID/publication clock established. | Storage/output/attribution grant for this application remains unverified. **Not selected for automated access.** |
| Footiqo | Dataset license explicitly permits personal non-commercial analysis, predictive models and backtests of legitimately obtained files; no operator-owned file supplied. | Manual exports/free samples or paid products; no public API. Automated downloads require written permission regardless of frequency; quota/cost depend on product/grant. | Historical datasets; coverage and updates depend on purchased product. Prospective fixture delivery and independently recorded result publication not established. | Local retention is permitted beyond update expiry; redistribution/commercial output/third-party rights remain restricted. No general attribution requirement identified on reviewed license, but actual product conditions still need review. **Eligible manual-file candidate; no automated transport selected.** |

Primary sources:
- API-Football [terms](https://www.api-football.com/terms), [pricing](https://www.api-football.com/pricing), [official API-Sports v3 reference](https://api-sports.io/documentation/football/v3), [official fixture guide](https://www.api-football.com/news/post/how-to-get-started-with-api-football-the-complete-beginners-guide).
- Football-Data [data overview](https://football-data.co.uk/data.php), [existing detailed assessment](football-data-assessment.md).
- Footiqo [terms](https://footiqo.com/terms-of-use/), [dataset license](https://footiqo.com/dataset-licence/), [products](https://footiqo.com/premium/). Reviewed license updated 8 October 2026; any product-specific conditions and third-party rights still need operator review.

## Technical contract candidate

`api-football-fixtures-v1` is an independent JSON interpretation, never the legacy
CSV profile. Only `/fixtures?league=...&season=...&from=...&to=...&timezone=UTC`
is supported, for one operator-pinned league/season and at most seven calendar days.
No odds, provider predictions, logos, statistics endpoints, scraping, RapidAPI or
arbitrary URLs are supported. The documented fixtures query does not list a page
parameter: reject multi-page envelopes instead of inventing one or silently truncating.
Cap response at 1 MiB / 500 fixtures. Reject duplicate JSON members, event IDs and
conflicts, count mismatches, nonempty error envelopes and out-of-scope fixture dates.

Require UTC, an explicit second-precision offset date and matching Unix timestamp
for usable kickoff evidence. Preserve missing/ambiguous kickoff as a blocker.
Retain known statuses verbatim; only NS/FT are candidate normal prematch/full-time
statuses. AET/PEN/awarded/walkover/cancelled/postponed/in-progress are excluded from
normal prediction/result use. Unknown statuses reject the payload. Null results
stay null; FT goals must agree with fulltime and half-time cannot exceed fulltime.
The endpoint supplies no independently verified publication clock in this contract.
HTTP Date, fixture date and retrieval time must never become source publication time.

No fixture is canonical or prediction-eligible merely because it parsed. Identity,
quality, source-bound kickoff, history, coverage and warm-up gates remain required.
Provider correction values describe separate source observations, not update authority.

## Activation blockers

No provider is selected as authorized. Required: operator confirmation of account
and intended use, purpose-specific grant including RAW/retention/analytics and any
required display/model rights, attribution/retention handling, actual league/season
entitlement and quota, operator-held key, and reviewed live persistence wiring.
A key or boolean switch is not proof of legal authorization. Existing SourcePolicy
approval must record evidence and all required purposes; unknown purposes deny.
