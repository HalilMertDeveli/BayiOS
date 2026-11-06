-- BayiOS operator tool (#129 item 7): end or shorten ONE tenant's plan trial, for UAT of the "trial ended → Basic" path.
-- Not an API endpoint, never called by the application. Documented in docs/operations/operator-tools.md.
--
--   psql "host=<db host> dbname=<db> user=bayios_app sslmode=require" \
--        -v ON_ERROR_STOP=1 -v tenant_id=<tenant uuid> [-v ends_at='2026-11-06T09:00:00Z'] \
--        -f infra/ops/shorten-trial.sql
--
-- Runs as bayios_app (NOBYPASSRLS), not as an owner or superuser: the change goes through the same row level security
-- as a request, inside the given tenant's context only, so the script cannot touch another tenant. (bayios_migrator
-- owns the table but FORCE RLS gives it no UPDATE policy on billing.tenant_subscriptions, by design.)
--
-- What it does, in one transaction:
--   * only a subscription in status 'trialing' is changed, and only made SHORTER (ends_at earlier than today's end);
--   * trial_ends_at := ends_at (default: one minute ago, so clock skew between servers cannot keep it open), never
--     before trial_started_at + 1 second (table CHECK);
--   * writes the audit event billing.trial_shortened (actor_role 'operator', no personal data).
-- Entitlements are derived on every request with time as input, so the tenant is on the fallback plan (Basic) as soon
-- as ends_at has passed; the hourly billing.subscription_expiry job then records status 'expired' and
-- billing.trial_expired as usual. No data is deleted. Re-running is harmless (nothing to shorten → 0 rows).
\set ON_ERROR_STOP on

\if :{?tenant_id}
\else
  \echo 'usage: psql ... -v tenant_id=<tenant uuid> [-v ends_at=<timestamptz>] -f infra/ops/shorten-trial.sql'
  \quit
\endif

\if :{?ends_at}
\else
  \set ends_at ''
\endif

BEGIN;

DO $$
BEGIN
    IF current_user <> 'bayios_app' THEN
        RAISE EXCEPTION 'run this script as bayios_app (RLS-bound), not as %', current_user;
    END IF;
END $$;

SELECT set_config('app.tenant_id', :'tenant_id'::uuid::text, true) AS tenant_context \gset

WITH shortened AS (
    UPDATE billing.tenant_subscriptions s
    SET trial_ends_at = greatest(coalesce(nullif(:'ends_at', '')::timestamptz, now() - interval '1 minute'), s.trial_started_at + interval '1 second'),
        row_version = s.row_version + 1,
        updated_at = now()
    WHERE s.tenant_id = :'tenant_id'::uuid
      AND s.status = 'trialing'
      AND greatest(coalesce(nullif(:'ends_at', '')::timestamptz, now() - interval '1 minute'), s.trial_started_at + interval '1 second') < s.trial_ends_at
    RETURNING s.id, s.tenant_id, s.trial_ends_at
)
INSERT INTO audit.audit_events (id, tenant_id, actor_user_id, actor_role, action, entity_type, entity_id, changes, correlation_id, occurred_at)
SELECT gen_random_uuid(), tenant_id, NULL, 'operator', 'billing.trial_shortened', 'tenant_subscription', id,
       jsonb_build_object('trialEndsAt', trial_ends_at, 'tool', 'infra/ops/shorten-trial.sql'),
       'ops-shorten-trial', now()
FROM shortened;

SELECT tenant_id, plan_code, status, trial_started_at, trial_ends_at, trial_ends_at <= now() AS trial_over
FROM billing.tenant_subscriptions
WHERE tenant_id = :'tenant_id'::uuid;

COMMIT;
