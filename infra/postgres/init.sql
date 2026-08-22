-- Panda Pocket: database-per-service bootstrap
--
-- One Postgres container hosts three logically isolated databases. Each has
-- exactly one login role, and that role can reach no other database. This is a
-- deliberate trade of failure isolation for local resource cost; the report
-- states it openly.
--
-- This script is executed once, by the postgres superuser, on first container
-- start (docker-entrypoint-initdb.d). Deleting the pgdata volume re-runs it.
--
-- The passwords below are local development credentials only. They are
-- committed on purpose so that `docker compose up` is reproducible from a
-- clean clone, which is part of the deployment criterion.

-- ---------------------------------------------------------------------------
-- 1. One login role per service
-- ---------------------------------------------------------------------------
CREATE ROLE merchant_svc   LOGIN PASSWORD 'merchant_pw_dev';
CREATE ROLE invoice_svc    LOGIN PASSWORD 'invoice_pw_dev';
CREATE ROLE settlement_svc LOGIN PASSWORD 'settlement_pw_dev';

-- ---------------------------------------------------------------------------
-- 2. One database per service, owned by that service's role
--    Ownership is what lets EF Core migrations create tables without the
--    superuser ever being used by an application.
-- ---------------------------------------------------------------------------
CREATE DATABASE merchant_db   OWNER merchant_svc;
CREATE DATABASE invoice_db    OWNER invoice_svc;
CREATE DATABASE settlement_db OWNER settlement_svc;

-- ---------------------------------------------------------------------------
-- 3. Close the default door
--    Postgres grants CONNECT on every new database to PUBLIC, which means every
--    role. Without this revoke the isolation is decorative. With it, connecting
--    as invoice_svc to merchant_db is refused, and that refusal is a demo asset.
-- ---------------------------------------------------------------------------
REVOKE CONNECT ON DATABASE merchant_db   FROM PUBLIC;
REVOKE CONNECT ON DATABASE invoice_db    FROM PUBLIC;
REVOKE CONNECT ON DATABASE settlement_db FROM PUBLIC;

GRANT CONNECT ON DATABASE merchant_db   TO merchant_svc;
GRANT CONNECT ON DATABASE invoice_db    TO invoice_svc;
GRANT CONNECT ON DATABASE settlement_db TO settlement_svc;

-- ---------------------------------------------------------------------------
-- 4. Schema ownership
--    From Postgres 15 onwards PUBLIC no longer holds CREATE on schema public,
--    so a migration running as the service role would fail with "permission
--    denied for schema public". Handing the schema to the owning role fixes it
--    without widening anything.
-- ---------------------------------------------------------------------------
\connect merchant_db
ALTER SCHEMA public OWNER TO merchant_svc;

\connect invoice_db
ALTER SCHEMA public OWNER TO invoice_svc;

\connect settlement_db
ALTER SCHEMA public OWNER TO settlement_svc;
