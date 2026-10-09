# BetStats football dashboard

Open the local dashboard and choose English or Polish. Dashboard, Matches, Teams, Models, Backtesting and Data quality share the competition/season filters. Matches also support team, date and status filters. Use Apply filters and Previous/Next to browse pages. Keyboard focus is visible; match evidence expands using Enter or Space.

Dates without a known kickoff remain date-only. Scheduled, postponed, completed, unknown and conflicting evidence are distinct. A score is shown only when current eligible regulation-time evidence supports it. The knowledge cutoff belongs to the frozen dataset; later result evidence does not rewrite that dataset.

Match details show dataset identity/hash, cutoff, historical evidence and exclusions. Probabilities appear only when a verified finalized artifact exists. No model runs when opening a page. Model views show the stored Elo, Poisson or Dixon–Coles definition/version, warm-up, expected goals and exact score distribution where supported. Binary probabilities are ordered no/yes; 1X2 is home/draw/away.

Backtesting displays stored sample counts, metric versions, denominators, calibration and exclusions. Unavailable and ineligible values are not zero. Reports with unequal evidence are not ranked; an eligible-sample intersection is available only for equivalent contracts. Synthetic execution is explicitly identified. Metrics apply to the stored report's original samples.

Data quality describes verified snapshot evidence, missing history, exclusions and date precision. It does not certify complete source-wide history or model readiness. An empty view means no verified matching artifacts. A denied view requires a source rights review; an error requires checking API connectivity and evidence integrity, then retrying Apply filters.

DEMO means fictional presentation data throughout the application. It never claims observed accuracy or initializes production data. The local setup is documented in `docs/en/development/dashboard.md`.
