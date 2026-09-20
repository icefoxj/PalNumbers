-- PalNumbers — database schema.
--
-- This script runs when the database is created and again on every open:
-- everything here is idempotent (IF NOT EXISTS), so opening a database that
-- already holds data changes nothing. A new table is added without touching
-- the ones that already have rows.
--
-- Table and column names are Portuguese because that is how they were first
-- created and there are millions of rows behind them. Renaming would mean
-- migrating a live database for nothing but spelling.
--
-- A number travels down a cascade of increasing limits:
--
--   main sequence (limit 10,000)
--        |  failed
--        v
--   NaoConvergiram  --(4 threads, limit 100,000)-->  converged: Palindromos
--        |  failed
--        v
--   NaoConvergiram100k  --(1 thread, limit 1,000,000)-->  converged: Palindromos
--        |  failed
--        v
--   NaoConvergiram1M  (terminal)
--
-- The limits grow because cost grows with the square of the iteration count:
-- 100,000 take about nine seconds per number and 1,000,000 about fifteen
-- minutes. The cheap threads sift, and the expensive one sees only what is
-- left.

PRAGMA journal_mode = WAL;      -- allows reading the database while it is written
PRAGMA synchronous  = NORMAL;   -- enough with WAL, and much faster

-- What appears on screen: the source number, the palindrome found and how many
-- reverse-and-add iterations it took. Receives both the numbers from the main
-- sequence and the ones rescued by reprocessing.
CREATE TABLE IF NOT EXISTS Palindromos (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Numero      TEXT    NOT NULL UNIQUE,
    Palindromo  TEXT    NOT NULL,
    Iteracoes   INTEGER NOT NULL,
    CalculadoEm TEXT    NOT NULL
);

-- First step of the cascade: failed the main sequence's limit of 10,000 and
-- waits for the triage threads.
--
-- The value reached is stored in none of these tables: it is not a result but
-- an intermediate nobody reads — reprocessing restarts from the source number.
-- Keeping it cost about 4 KB per row and accounted for most of the database
-- size. Digitos preserves the part worth analysing.
CREATE TABLE IF NOT EXISTS NaoConvergiram (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Numero      TEXT    NOT NULL UNIQUE,
    Iteracoes   INTEGER NOT NULL,
    Digitos     INTEGER NOT NULL,
    CalculadoEm TEXT    NOT NULL
);

-- Second step: survived the triage at 100,000 and waits for the thread that
-- attempts the full million.
CREATE TABLE IF NOT EXISTS NaoConvergiram100k (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Numero      TEXT    NOT NULL UNIQUE,
    Iteracoes   INTEGER NOT NULL,
    Digitos     INTEGER NOT NULL,
    CalculadoEm TEXT    NOT NULL
);

-- End of the cascade: survived 1,000,000 iterations. Nothing reprocesses these.
CREATE TABLE IF NOT EXISTS NaoConvergiram1M (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Numero      TEXT    NOT NULL UNIQUE,
    Iteracoes   INTEGER NOT NULL,
    Digitos     INTEGER NOT NULL,
    CalculadoEm TEXT    NOT NULL
);
