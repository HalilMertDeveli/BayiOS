namespace BayiOS.IntegrationTests.Security;

/// <summary>pg_catalog queries shared by the database guards and the attack sweeps.</summary>
internal static class DatabaseCatalog
{
    /// <summary>
    /// BayiOS business schemas: every schema the migrator owns, except <c>extensions</c> (pg_trgm lives there; it holds
    /// no tables and its functions belong to the extension). A new module schema is covered automatically.
    /// </summary>
    public const string BusinessSchemasSql =
        """
        SELECT n.nspname FROM pg_namespace n
        WHERE pg_get_userbyid(n.nspowner) = 'bayios_migrator' AND n.nspname <> 'extensions'
        """;

    /// <summary>Qualified names (<c>schema.table</c>) of every business table with a <c>tenant_id</c> column.</summary>
    public const string TenantTablesSql =
        $"""
        SELECT quote_ident(n.nspname) || '.' || quote_ident(c.relname)
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r', 'p') AND n.nspname IN ({BusinessSchemasSql})
          AND EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped)
        ORDER BY 1
        """;
}
