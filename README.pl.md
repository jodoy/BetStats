# BetStats 2.0

Wielosportowa platforma danych, predykcji probabilistycznych, symulacji oraz operacji wspomaganych przez AI.

> **Status:** baseline architektury v1.0 / bootstrap implementacji.

## Granica produktu

BetStats **nie jest bukmacherem**. Nie przyjmuje prawdziwych stawek, depozytów ani wypłat i nie wykonuje zakładów. Prediction Playground korzysta wyłącznie z wirtualnych kuponów do analizy, edukacji i zabawy.

## Pierwszy vertical slice

`Football → jedna rozgrywka → jeden dozwolony darmowy provider → RAW → Observation → Canonical → FeatureSnapshot → PredictionSnapshot → Evaluation`

Przed zmianami przeczytaj `AGENTS.md`. Dane zewnętrzne podlegają własnym licencjom i nie są objęte licencją kodu repozytorium.
