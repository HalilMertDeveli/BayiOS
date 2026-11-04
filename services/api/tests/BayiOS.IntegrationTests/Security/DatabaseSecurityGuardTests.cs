using BayiOS.IntegrationTests.Infrastructure;
using Npgsql;

namespace BayiOS.IntegrationTests.Security;

/// <summary>
/// Catalogue-driven database guards (ADR 0002, data-model §2, threat-model "tenant escape"). They read pg_catalog for
/// every table of every BayiOS business schema, so a table added by any module — present or future — is checked
/// without touching this file, and fails until its grants are declared in <see cref="DeclaredGrants"/>.
/// Every accepted exception is listed here with its reason; nothing else is exempt.
/// </summary>
[Trait("Category", "Security")]
public sealed class DatabaseSecurityGuardTests(ApiFixture fixture)
{
    private const string App = "bayios_app";

    private const string Select = "SELECT";
    private const string Insert = "INSERT";
    private const string Update = "UPDATE";
    private const string Delete = "DELETE";

    private static readonly string[] AppendOnly = [Select, Insert];
    private static readonly string[] ArchiveOnly = [Select, Insert, Update];
    private static readonly string[] Full = [Select, Insert, Update, Delete];

    /// <summary>
    /// What each module's migration grants <c>bayios_app</c> — exactly; any extra privilege fails (data-model §2.1).
    /// Append-only tables (ledger, audit, order history/shipments) never get UPDATE/DELETE; catalog and other
    /// "archive, never delete" tables never get DELETE.
    /// </summary>
    private static readonly Dictionary<string, string[]> DeclaredGrants = new Dictionary<string, string[]>
    {
        ["audit.audit_events"] = AppendOnly,

        ["catalog.brands"] = ArchiveOnly,
        ["catalog.categories"] = ArchiveOnly,
        ["catalog.custom_field_definitions"] = ArchiveOnly,
        ["catalog.product_variants"] = ArchiveOnly,
        ["catalog.products"] = ArchiveOnly,
        ["catalog.units"] = ArchiveOnly,

        ["customers.customer_addresses"] = Full, // addresses are hard-deleted (customers-and-pricing §2)
        ["customers.customers"] = ArchiveOnly,
        ["customers.dealer_groups"] = ArchiveOnly,

        ["identity.memberships"] = Full,
        ["identity.tenant_settings"] = Full,
        ["identity.tenants"] = Full,
        ["identity.user_profiles"] = Full,

        ["imports.import_files"] = [Select, Insert, Delete], // file bytes are purged after the retention window
        ["imports.import_jobs"] = ArchiveOnly,
        ["imports.import_rows"] = Full,
        ["imports.mapping_templates"] = Full,

        ["inventory.inventory_balances"] = ArchiveOnly, // derived; never deleted
        ["inventory.inventory_movements"] = AppendOnly, // the ledger
        ["inventory.stock_reservations"] = ArchiveOnly,

        ["orders.order_lines"] = Full, // DELETE only while the order is a draft (trigger trg_order_lines_delete_draft_only)
        ["orders.order_number_sequences"] = ArchiveOnly,
        ["orders.order_shipment_lines"] = AppendOnly,
        ["orders.order_shipments"] = AppendOnly,
        ["orders.order_status_history"] = AppendOnly,
        ["orders.orders"] = ArchiveOnly,

        ["platform.idempotency_keys"] = Full, // purged by platform.purge_expired_idempotency_keys
        ["platform.jobs"] = Full,
        ["platform.outbox_messages"] = Full,

        ["pricing.price_list_items"] = Full,
        ["pricing.price_lists"] = ArchiveOnly,

        ["warehouse.warehouse_locations"] = ArchiveOnly,
        ["warehouse.warehouses"] = ArchiveOnly,
    };

    /// <summary>Tables that must never lose or change a row once written (append-only by grant AND by trigger).</summary>
    private static readonly Dictionary<string, string> AppendOnlyTriggers = new Dictionary<string, string>
    {
        ["inventory.inventory_movements"] = "inventory.reject_ledger_change",
        ["audit.audit_events"] = "audit.reject_audit_change",
    };

    /// <summary>
    /// Accepted exception (data-model §2.3): the multi-tenant job queue and outbox are read by one dispatcher across
    /// tenants, so they have no RLS; instead a trigger rejects writing a row for another tenant while a tenant
    /// context is set (§5.12). Only <c>bayios_app</c> can reach them.
    /// </summary>
    private static readonly Dictionary<string, string> NoRlsWithTenantTrigger = new Dictionary<string, string>
    {
        ["platform.jobs"] = "platform.enforce_queue_tenant",
        ["platform.outbox_messages"] = "platform.enforce_queue_tenant",
    };

    /// <summary>
    /// Tenant tables whose policy is not a plain <c>tenant_id = app.tenant_id</c> (data-model §2.3). They still must
    /// reference <c>app.tenant_id</c>; the extra branch is the documented one.
    /// </summary>
    private static readonly Dictionary<string, string> PolicyExceptions = new Dictionary<string, string>
    {
        ["identity.memberships"] = "read: tenant_id = app.tenant_id OR user_id = app.user_id (tenant resolution)",
        ["platform.idempotency_keys"] = "tenant_id = app.tenant_id OR (tenant_id IS NULL AND user_id = app.user_id); migrator purge policies",
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Business_schemas_are_the_known_modules_and_nothing_lives_in_public()
    {
        var schemas = await StringsAsync(DatabaseCatalog.BusinessSchemasSql + " ORDER BY 1");
        Assert.Superset(
            new HashSet<string> { "audit", "catalog", "customers", "identity", "imports", "inventory", "orders", "platform", "pricing", "warehouse" },
            schemas.ToHashSet());

        Assert.Empty(await StringsAsync(
            "SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND c.relkind IN ('r','p','v','m','S')"));
    }

    [Fact]
    public async Task Every_tenant_table_has_forced_rls_and_a_policy_for_the_runtime_role()
    {
        var rows = await RowsAsync(
            $"""
            SELECT quote_ident(n.nspname) || '.' || quote_ident(c.relname), c.relrowsecurity, c.relforcerowsecurity,
                   (SELECT count(*) FROM pg_policies p
                    WHERE p.schemaname = n.nspname AND p.tablename = c.relname AND '{App}' = ANY (p.roles))
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p') AND n.nspname IN ({DatabaseCatalog.BusinessSchemasSql}) AND c.relname <> '__ef_migrations_history'
            ORDER BY 1
            """);

        var violations = new List<string>();
        var tenantTables = (await StringsAsync(DatabaseCatalog.TenantTablesSql)).ToHashSet();
        foreach (var row in rows)
        {
            var (table, enabled, forced, policies) = ((string)row[0], (bool)row[1], (bool)row[2], (long)row[3]);
            if (NoRlsWithTenantTrigger.ContainsKey(table))
            {
                if (enabled || policies > 0)
                {
                    violations.Add($"{table}: listed as a no-RLS queue table but now has RLS/policies — move it out of the allowlist");
                }

                continue;
            }

            if (!enabled || !forced)
            {
                violations.Add($"{table}: RLS enabled={enabled} forced={forced} (both required)");
            }

            if (policies == 0)
            {
                violations.Add($"{table}: no policy for {App}");
            }
        }

        // Tables without tenant_id (identity.tenants, identity.user_profiles) are covered too; sanity-check the scan.
        Assert.True(tenantTables.Count >= 25, $"Only {tenantTables.Count} tenant tables found — catalogue query broken?");
        Assert.Empty(violations);
    }

    [Fact]
    public async Task Every_policy_is_scoped_to_the_runtime_role_and_to_the_request_context()
    {
        var rows = await RowsAsync(
            $"""
            SELECT quote_ident(p.schemaname) || '.' || quote_ident(p.tablename), p.policyname, p.roles::text[], p.cmd,
                   coalesce(p.qual, ''), coalesce(p.with_check, ''), p.permissive
            FROM pg_policies p WHERE p.schemaname IN ({DatabaseCatalog.BusinessSchemasSql})
            ORDER BY 1, 2
            """);
        var tenantTables = (await StringsAsync(DatabaseCatalog.TenantTablesSql)).ToHashSet();

        var violations = new List<string>();
        var referencesTenant = new HashSet<string>();
        foreach (var row in rows)
        {
            var (table, name, roles, cmd, qual, check) = ((string)row[0], (string)row[1], (string[])row[2], (string)row[3], (string)row[4], (string)row[5]);
            if (roles.Contains("public"))
            {
                violations.Add($"{table}.{name}: policy applies to PUBLIC");
            }

            if (!roles.Contains(App))
            {
                // Only the documented migrator purge policies on idempotency keys may target another role.
                if (!(table == "platform.idempotency_keys" && roles.SequenceEqual(["bayios_migrator"])))
                {
                    violations.Add($"{table}.{name}: policy for unexpected role(s) {string.Join(',', roles)}");
                }

                continue;
            }

            var expressions = new[] { qual, check }.Where(e => e.Length > 0).ToArray();
            if (expressions.Length == 0)
            {
                violations.Add($"{table}.{name}: no USING / WITH CHECK expression");
            }

            foreach (var expression in expressions)
            {
                if (!expression.Contains("current_setting('app.", StringComparison.Ordinal))
                {
                    violations.Add($"{table}.{name} ({cmd}): expression does not depend on the request context: {expression}");
                }
            }

            if (cmd is "INSERT" or "UPDATE" && check.Length == 0)
            {
                violations.Add($"{table}.{name} ({cmd}): write policy without WITH CHECK");
            }

            if (qual.Contains("app.tenant_id", StringComparison.Ordinal) || check.Contains("app.tenant_id", StringComparison.Ordinal))
            {
                referencesTenant.Add(table);
            }
        }

        foreach (var table in tenantTables.Where(t => !NoRlsWithTenantTrigger.ContainsKey(t) && !referencesTenant.Contains(t)))
        {
            violations.Add($"{table}: has tenant_id but no {App} policy references app.tenant_id");
        }

        Assert.Empty(violations);
        Assert.All(PolicyExceptions.Keys, t => Assert.Contains(t, tenantTables));
    }

    [Fact]
    public async Task Runtime_role_grants_are_exactly_what_each_module_declared()
    {
        var rows = await RowsAsync(
            $"""
            SELECT quote_ident(n.nspname) || '.' || quote_ident(c.relname), c.relkind::text,
                   coalesce((SELECT array_agg(a.privilege_type ORDER BY a.privilege_type)
                             FROM aclexplode(c.relacl) a WHERE a.grantee = '{App}'::regrole), ARRAY[]::text[]),
                   coalesce((SELECT array_agg(a.privilege_type ORDER BY a.privilege_type)
                             FROM aclexplode(c.relacl) a WHERE a.grantee = 0), ARRAY[]::text[])
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f') AND n.nspname IN ({DatabaseCatalog.BusinessSchemasSql})
            ORDER BY 1
            """);

        var violations = new List<string>();
        var seen = new HashSet<string>();
        foreach (var row in rows)
        {
            var (table, kind, granted, toPublic) = ((string)row[0], (string)row[1], (string[])row[2], (string[])row[3]);
            seen.Add(table);
            if (toPublic.Length > 0)
            {
                violations.Add($"{table}: PUBLIC has {string.Join(',', toPublic)}");
            }

            if (kind != "r")
            {
                // Views/materialized views run as their owner (the migrator) and would bypass RLS; sequences/foreign tables are unexpected.
                violations.Add($"{table}: unexpected relation kind '{kind}' in a business schema — review RLS impact and declare it");
                continue;
            }

            if (table.EndsWith(".__ef_migrations_history", StringComparison.Ordinal))
            {
                if (granted.Length > 0)
                {
                    violations.Add($"{table}: {App} must not reach the migrations history (has {string.Join(',', granted)})");
                }

                continue;
            }

            if (!DeclaredGrants.TryGetValue(table, out var declared))
            {
                violations.Add($"{table}: new table — declare its {App} grants in DeclaredGrants (has {string.Join(',', granted)})");
                continue;
            }

            var extra = granted.Except(declared).ToArray();
            var missing = declared.Except(granted).ToArray();
            if (extra.Length > 0 || missing.Length > 0)
            {
                violations.Add($"{table}: extra [{string.Join(',', extra)}] missing [{string.Join(',', missing)}]");
            }
        }

        violations.AddRange(DeclaredGrants.Keys.Where(t => !seen.Contains(t)).Select(t => $"{t}: declared but no longer exists — remove it"));

        // Column-level grants would sit outside this table-level review.
        violations.AddRange(await StringsAsync(
            $"""
            SELECT n.nspname || '.' || c.relname || '.' || a.attname || ': column-level grant'
            FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ({DatabaseCatalog.BusinessSchemasSql}) AND a.attacl IS NOT NULL
            """));

        Assert.Empty(violations);
    }

    [Fact]
    public async Task Append_only_tables_cannot_change_even_by_mistake()
    {
        foreach (var table in DeclaredGrants.Where(g => g.Value == AppendOnly).Select(g => g.Key))
        {
            Assert.False(await ScalarAsync<bool>($"SELECT has_table_privilege('{App}', '{table}', 'UPDATE') OR has_table_privilege('{App}', '{table}', 'DELETE') OR has_table_privilege('{App}', '{table}', 'TRUNCATE')"), table);
        }

        foreach (var table in DeclaredGrants.Where(g => g.Value == ArchiveOnly).Select(g => g.Key))
        {
            Assert.False(await ScalarAsync<bool>($"SELECT has_table_privilege('{App}', '{table}', 'DELETE') OR has_table_privilege('{App}', '{table}', 'TRUNCATE')"), table);
        }

        // The ledger and the audit trail are also protected against their owner (UPDATE/DELETE/TRUNCATE triggers).
        foreach (var (table, function) in AppendOnlyTriggers)
        {
            var triggers = await RowsAsync(
                $"""
                SELECT t.tgname, t.tgenabled::text, p.oid::regprocedure::text
                FROM pg_trigger t JOIN pg_proc p ON p.oid = t.tgfoid
                WHERE t.tgrelid = '{table}'::regclass AND NOT t.tgisinternal
                """);
            Assert.True(triggers.Count >= 2, $"{table}: expected row and TRUNCATE guards, found {triggers.Count}");
            Assert.All(triggers, t =>
            {
                Assert.NotEqual("D", (string)t[1]);
                Assert.StartsWith(function + "(", (string)t[2], StringComparison.Ordinal);
            });
        }
    }

    [Fact]
    public async Task Queue_tables_without_rls_are_guarded_by_the_tenant_trigger()
    {
        foreach (var (table, function) in NoRlsWithTenantTrigger)
        {
            Assert.True(await ScalarAsync<bool>($"SELECT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = '{table}'::regclass AND attname = 'tenant_id')"), table);
            var triggers = await RowsAsync(
                $"""
                SELECT t.tgenabled::text, p.oid::regprocedure::text,
                       (t.tgtype & 2) <> 0 AS before, (t.tgtype & 4) <> 0 AS on_insert, (t.tgtype & 16) <> 0 AS on_update
                FROM pg_trigger t JOIN pg_proc p ON p.oid = t.tgfoid
                WHERE t.tgrelid = '{table}'::regclass AND NOT t.tgisinternal
                """);
            var trigger = Assert.Single(triggers);
            Assert.NotEqual("D", (string)trigger[0]);
            Assert.StartsWith(function + "(", (string)trigger[1], StringComparison.Ordinal);
            Assert.True((bool)trigger[2] && (bool)trigger[3] && (bool)trigger[4], $"{table}: trigger must run BEFORE INSERT OR UPDATE");
        }
    }

    [Fact]
    public async Task Security_definer_functions_pin_search_path_and_are_not_public()
    {
        var rows = await RowsAsync(
            """
            SELECT p.oid::regprocedure::text, coalesce(p.proconfig, '{}'::text[]), pg_get_userbyid(p.proowner),
                   p.proacl IS NULL OR EXISTS (SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee = 0 AND a.privilege_type = 'EXECUTE')
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE p.prosecdef AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            ORDER BY 1
            """);

        var violations = new List<string>();
        foreach (var row in rows)
        {
            var (function, config, owner, publicExecute) = ((string)row[0], (string[])row[1], (string)row[2], (bool)row[3]);
            if (!config.Any(c => c.StartsWith("search_path=", StringComparison.Ordinal)))
            {
                violations.Add($"{function}: SECURITY DEFINER without a pinned search_path");
            }

            if (owner == App)
            {
                violations.Add($"{function}: owned by {App}");
            }

            if (publicExecute)
            {
                violations.Add($"{function}: SECURITY DEFINER executable by PUBLIC");
            }
        }

        Assert.Empty(violations);
        Assert.NotEmpty(rows); // platform.purge_expired_idempotency_keys exists today; an empty scan means the query broke.
    }

    [Fact]
    public async Task Runtime_role_owns_nothing_and_cannot_escalate()
    {
        var role = Assert.Single(await RowsAsync(
            $"SELECT rolsuper, rolbypassrls, rolcreaterole, rolcreatedb, rolreplication FROM pg_roles WHERE rolname = '{App}'"));
        Assert.All(role, flag => Assert.False((bool)flag));

        // Not a member of any role (memberships would inherit its attributes/privileges).
        Assert.Empty(await StringsAsync(
            $"SELECT r.rolname FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.roleid WHERE m.member = '{App}'::regrole"));

        Assert.Equal(0L, await ScalarAsync<long>(
            $"""
            SELECT (SELECT count(*) FROM pg_class WHERE relowner = '{App}'::regrole)
                 + (SELECT count(*) FROM pg_namespace WHERE nspowner = '{App}'::regrole)
                 + (SELECT count(*) FROM pg_proc WHERE proowner = '{App}'::regrole)
                 + (SELECT count(*) FROM pg_type WHERE typowner = '{App}'::regrole)
            """));

        // No DDL anywhere: cannot create objects in a business schema or in public.
        Assert.Empty(await StringsAsync(
            $"""
            SELECT n.nspname FROM pg_namespace n
            WHERE (n.nspname IN ({DatabaseCatalog.BusinessSchemasSql}) OR n.nspname IN ('public', 'extensions'))
              AND has_schema_privilege('{App}', n.oid, 'CREATE')
            """));
    }

    [Fact]
    public async Task Public_and_supabase_client_roles_have_no_access_to_business_schemas()
    {
        var violations = await StringsAsync(
            $"""
            SELECT n.nspname || ': ' || CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE a.grantee::regrole::text END || ' ' || a.privilege_type
            FROM pg_namespace n CROSS JOIN LATERAL aclexplode(n.nspacl) a
            WHERE n.nspname IN ({DatabaseCatalog.BusinessSchemasSql})
              AND a.grantee NOT IN ('{App}'::regrole, 'bayios_migrator'::regrole)
            """);
        Assert.Empty(violations);

        // Supabase client roles exist only on Supabase; where they do, they must not reach business data (data-model §2.1).
        foreach (var clientRole in new[] { "anon", "authenticated", "service_role" })
        {
            if (await ScalarAsync<bool>($"SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{clientRole}')"))
            {
                Assert.Empty(await StringsAsync(
                    $"SELECT nspname FROM pg_namespace WHERE nspname IN ({DatabaseCatalog.BusinessSchemasSql}) AND has_schema_privilege('{clientRole}', oid, 'USAGE')"));
            }
        }
    }

    [Fact]
    public async Task Without_or_with_a_foreign_tenant_context_the_runtime_role_sees_no_rows()
    {
        // Behavioural check over the same catalogue: as bayios_app, no context → nothing; a context for a tenant that
        // owns nothing → nothing, in every RLS table, whatever other tests have written meanwhile.
        var tables = (await StringsAsync(DatabaseCatalog.TenantTablesSql)).Where(t => !NoRlsWithTenantTrigger.ContainsKey(t)).ToList();
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync(Ct);
        foreach (var tenant in new[] { string.Empty, Guid.NewGuid().ToString() })
        {
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            await using (var context = new NpgsqlCommand("SELECT set_config('app.tenant_id', $1, true), set_config('app.user_id', $2, true)", connection, transaction))
            {
                context.Parameters.Add(new NpgsqlParameter { Value = tenant });
                context.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid().ToString() });
                await context.ExecuteNonQueryAsync(Ct);
            }

            foreach (var table in tables)
            {
                await using var count = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection, transaction);
                Assert.True((long)(await count.ExecuteScalarAsync(Ct))! == 0, $"{table} shows rows for tenant context '{tenant}'");
            }

            await transaction.RollbackAsync(Ct);
        }
    }

    private async Task<List<object[]>> RowsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.SuperuserConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<object[]>();
        while (await reader.ReadAsync(Ct))
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(values);
        }

        return rows;
    }

    private async Task<List<string>> StringsAsync(string sql) => [.. (await RowsAsync(sql)).Select(r => (string)r[0])];

    private async Task<T> ScalarAsync<T>(string sql) => (T)(await RowsAsync(sql))[0][0];
}
