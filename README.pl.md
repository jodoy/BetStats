# BetStats 2.0

Wielosportowa platforma danych, predykcji probabilistycznych, symulacji oraz operacji wspomaganych przez AI.

> **Status:** baseline architektury v1.0 / bootstrap implementacji.

## Granica produktu

BetStats **nie jest bukmacherem**. Nie przyjmuje prawdziwych stawek, depozytów ani wypłat i nie wykonuje zakładów. Prediction Playground korzysta wyłącznie z wirtualnych kuponów do analizy, edukacji i zabawy.

## Pierwszy vertical slice

`Football → jedna rozgrywka → jeden dozwolony darmowy provider → RAW → Observation → Canonical → FeatureSnapshot → PredictionSnapshot → Evaluation`

Przed zmianami przeczytaj `AGENTS.md`. Dane zewnętrzne podlegają własnym licencjom i nie są objęte licencją kodu repozytorium.

## Dokumentacja

- 🇬🇧 [Software & Product Engineering Specification v1.0 — English](docs/specifications/BetStats_2_0_Software_Product_Engineering_Specification_v1_0_EN.docx)
- 🇵🇱 [Specyfikacja Produktu i Inżynierii v1.0 — Polski](docs/specifications/BetStats_2_0_Specyfikacja_Produktu_i_Inzynierii_v1_0_PL.docx)
- ADR: `docs/adr/`
- Dokumentacja angielska: `docs/en/`
- Dokumentacja polska: `docs/pl/`

Markdown jest dokumentacją żywą; pliki DOCX są wersjonowanym baseline specyfikacji.
