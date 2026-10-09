# Pulpit piłkarski BetStats

Otwórz lokalny dashboard i wybierz język polski lub angielski. Dostępne widoki: Pulpit, Mecze, Drużyny, Modele, Testy historyczne i Jakość danych. Wybierz rozgrywki i sezon, a dla meczów również drużynę, zakres dat i status. Zastosuj filtry; przyciski Wstecz/Dalej zmieniają stronę. Widoczny fokus ułatwia obsługę klawiaturą, a szczegóły meczu otwiera Enter lub Spacja.

Nieznana godzina pozostaje nieznana: data nie jest zamieniana na fikcyjną godzinę rozpoczęcia. Zaplanowany, przełożony, zakończony, nieznany i sprzeczny status są rozróżniane. Wynik pochodzi wyłącznie z aktualnych kwalifikujących się dowodów dotyczących regulaminowego czasu gry. Granica wiedzy dotyczy zamrożonego zbioru; późniejszy wynik nie zmienia jego historii.

Szczegóły zawierają identyfikator i hash zbioru, granicę wiedzy, liczbę dowodów i wykluczenia. Prawdopodobieństwa wymagają zweryfikowanego, sfinalizowanego artefaktu. Otwarcie strony nie uruchamia modelu. Modele pokazują zapisaną wersję Elo, Poissona lub Dixona–Colesa, rozgrzewkę oraz dostępne oczekiwane gole i rozkład dokładnych wyników. Kolejność 1X2: gospodarze/remis/goście; zmienne binarne: nie/tak.

Testy historyczne pokazują zapisane metryki, wersje, mianowniki, kalibrację i powody wykluczeń. Wartości niedostępne i niekwalifikujące się nie są zerami. Brak rankingu nieporównywalnych raportów; wspólne próbki są liczone tylko dla równoważnych kontraktów. Wykonanie syntetyczne jest oznaczone. Metryki dotyczą oryginalnych próbek raportu.

Jakość danych opisuje dowody snapshotów, braki historii, wykluczenia i dokładność dat. Nie potwierdza kompletności całego źródła ani gotowości do trenowania. Pusty widok oznacza brak zweryfikowanych artefaktów. Odmowa dostępu wymaga przeglądu praw źródła; błąd wymaga sprawdzenia API i integralności dowodów, a następnie ponownego zastosowania filtrów.

DEMO oznacza fikcyjne dane prezentacyjne w każdym widoku, bez deklarowanej zaobserwowanej skuteczności i bez inicjalizacji danych produkcyjnych. Dokładne polecenia uruchomienia opisano w `docs/en/development/dashboard.md`.
