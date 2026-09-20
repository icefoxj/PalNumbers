-- PalNumbers — esquema do banco.
--
-- Este script roda na criação do banco e também a cada abertura: tudo aqui é
-- idempotente (IF NOT EXISTS), então abrir um banco já existente não altera
-- nem apaga nada. Uma tabela nova é acrescentada sem tocar nas que já têm
-- dados.
--
-- O fluxo de um número é uma cascata de limites crescentes:
--
--   sequência principal (limite 10.000)
--        |  falhou
--        v
--   NaoConvergiram  --(4 threads, limite 100.000)-->  convergiu: Palindromos
--        |  falhou
--        v
--   NaoConvergiram100k  --(1 thread, limite 1.000.000)-->  convergiu: Palindromos
--        |  falhou
--        v
--   NaoConvergiram1M  (terminal)
--
-- Os limites crescem porque o custo cresce com o quadrado das iterações:
-- 100.000 levam cerca de 9 segundos por número, 1.000.000 levam uns 15
-- minutos. As threads baratas filtram, e a cara só vê o que sobrou.

PRAGMA journal_mode = WAL;      -- permite ler o banco enquanto o programa grava
PRAGMA synchronous  = NORMAL;   -- suficiente com WAL, e muito mais rápido

-- Os valores que aparecem na tela: número de origem, palíndromo encontrado e
-- quantas iterações de "inverter e somar" foram necessárias. Recebe tanto os
-- da sequência principal quanto os resgatados pelo reprocessamento.
CREATE TABLE IF NOT EXISTS Palindromos (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Numero      TEXT    NOT NULL UNIQUE,
    Palindromo  TEXT    NOT NULL,
    Iteracoes   INTEGER NOT NULL,
    CalculadoEm TEXT    NOT NULL
);

-- Primeiro estágio da cascata: falharam no limite de 10.000 da sequência
-- principal e esperam a triagem das 4 threads.
--
-- O valor alcançado não é guardado em nenhuma destas tabelas: não é um
-- resultado, e sim um número intermediário que ninguém lê — o reprocessamento
-- recomeça do número de origem. Digitos preserva o dado analítico.
CREATE TABLE IF NOT EXISTS NaoConvergiram (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Numero      TEXT    NOT NULL UNIQUE,
    Iteracoes   INTEGER NOT NULL,
    Digitos     INTEGER NOT NULL,
    CalculadoEm TEXT    NOT NULL
);

-- Segundo estágio: sobreviveram à triagem de 100.000 e esperam a thread que
-- tenta o milhão completo.
CREATE TABLE IF NOT EXISTS NaoConvergiram100k (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Numero      TEXT    NOT NULL UNIQUE,
    Iteracoes   INTEGER NOT NULL,
    Digitos     INTEGER NOT NULL,
    CalculadoEm TEXT    NOT NULL
);

-- Fim da cascata: resistiram a 1.000.000 de iterações. Nada os reprocessa.
CREATE TABLE IF NOT EXISTS NaoConvergiram1M (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Numero      TEXT    NOT NULL UNIQUE,
    Iteracoes   INTEGER NOT NULL,
    Digitos     INTEGER NOT NULL,
    CalculadoEm TEXT    NOT NULL
);
