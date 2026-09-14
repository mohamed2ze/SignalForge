#!/usr/bin/env bash
# ------------------------------------------------------------------
# Least-privilege database bootstrap for the SignalForge app stack.
#
# The API and outbox worker connect to SQL Server as `signalforge_app`
# (db_owner of the SignalForge database) instead of the `sa` login, so the
# SA password is only ever used by this one-time/startup script and the
# healthcheck. db_owner is the smallest role that lets EF Core auto-migrate
# the schema at startup; the app never holds sysadmin rights.
#
# Idempotent: safe to run on every container start, fresh or already-seeded.
# ------------------------------------------------------------------
set -euo pipefail

APP_DB_PASSWORD="${APP_DB_PASSWORD:-}"
if [[ -z "$APP_DB_PASSWORD" ]]; then
  echo "[init] APP_DB_PASSWORD not set; skipping least-privilege login creation" >&2
  exit 0
fi

# SQL Server host/port. Defaults to localhost (entrypoint-style run inside the
# mssql container); the compose one-shot init service overrides with "mssql".
DB_HOST="${DB_HOST:-localhost}"

# T-SQL string literals escape a single quote by doubling it.
escaped=${APP_DB_PASSWORD//\'/\'\'}

# Login, database, then the application user. The database is created here
# (idempotently) so the user exists before the first app startup; EF Migrate
# then only applies schema migrations.
/opt/mssql-tools18/bin/sqlcmd \
  -S "$DB_HOST" -U sa -P "$MSSQL_SA_PASSWORD" -C -b -l 60 \
  -Q "
IF SUSER_ID(N'signalforge_app') IS NULL
    CREATE LOGIN [signalforge_app]
        WITH PASSWORD = N'$escaped',
             CHECK_POLICY = OFF,
             CHECK_EXPIRATION = OFF;
GO
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'SignalForge')
    CREATE DATABASE [SignalForge];
GO
USE [SignalForge];
IF DATABASE_PRINCIPAL_ID(N'signalforge_app') IS NULL
    CREATE USER [signalforge_app] FOR LOGIN [signalforge_app];
GO
IF NOT EXISTS (
    SELECT 1 FROM sys.database_role_members drm
    JOIN sys.database_principals r  ON r.principal_id = drm.role_principal_id
    JOIN sys.database_principals m  ON m.principal_id = drm.member_principal_id
    WHERE r.name = N'db_owner' AND m.name = N'signalforge_app')
    ALTER ROLE [db_owner] ADD MEMBER [signalforge_app];
GO
"