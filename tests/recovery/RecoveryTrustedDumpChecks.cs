using Platform.Recovery.Worker;

internal static class RecoveryTrustedDumpChecks
{
    internal static async Task RunAsync()
    {
        var tables=new Dictionary<string,string[]> { ["assets"]=["id","state"] };
        var copied=new StringWriter();var starts=0;
        await RecoveryTrustedDumpRestore.LoadCopyStreamAsync(new StringReader("""
            -- Header is inert
            \restrict ABCDEFG
            SET statement_timeout = 0;
            SELECT pg_catalog.set_config('search_path', '', false);
            COPY public.assets (id, state) FROM stdin;
            1	DROP TABLE public.assets;
            \.
            \unrestrict ABCDEFG
            """),tables,(_,_)=> { starts++;return Task.FromResult<TextWriter>(copied); },default);
        Require(starts==1 && copied.ToString().Contains("DROP TABLE",StringComparison.Ordinal),"copy_data_became_sql");
        foreach(var statement in new[] { "SELECT untrusted_function();", "CREATE FUNCTION danger();",
            "COPY public.foreign (id, state) FROM stdin;", "COPY public.assets (state, id) FROM stdin;",
            "COPY public.assets (\"id, state) FROM stdin;", "COPY public.assets FROM PROGRAM 'danger';" })
        {
            var invoked=false;
            try
            {
                await RecoveryTrustedDumpRestore.LoadCopyStreamAsync(new StringReader(statement),tables,
                    (_,_)=> { invoked=true;return Task.FromResult<TextWriter>(new StringWriter()); },default);
            }
            catch(RecoveryWorkerException error) when(error.SafeCode is "recovery_dump_data_command_untrusted" or "recovery_dump_data_columns_untrusted")
            { Require(!invoked,"untrusted_command_reached_database");continue; }
            throw new InvalidOperationException("Accepted executable dump command.");
        }
        await Reject("COPY public.assets (id, state) FROM stdin;\n1\tactive\n","recovery_dump_data_incomplete");
        await Reject("","recovery_dump_data_incomplete");
        await Reject("COPY public.assets (id, state) FROM stdin;\n\\.\nCOPY public.assets (id, state) FROM stdin;\n\\.\n",
            "recovery_dump_data_command_untrusted");
        var trusted="CREATE FUNCTION public.safe() RETURNS text LANGUAGE sql AS $$\n-- body comment\nSELECT 'value';\n$$;\n";
        Require(RecoveryTrustedDumpRestore.CanonicalSchema("-- generated header\n\\restrict TOKEN\n"+trusted+"\\unrestrict TOKEN\n")==
            RecoveryTrustedDumpRestore.CanonicalSchema(trusted),"generated_archive_header_changed_schema");
        Require(RecoveryTrustedDumpRestore.CanonicalSchema(trusted)!=RecoveryTrustedDumpRestore.CanonicalSchema(
            trusted.Replace("-- body comment","-- changed executable body context",StringComparison.Ordinal)),"function_body_comments_were_discarded");
        Require(RecoveryTrustedDumpRestore.CanonicalSchema(trusted)!=RecoveryTrustedDumpRestore.CanonicalSchema(
            trusted.Replace("SELECT 'value'","SELECT 'tampered'",StringComparison.Ordinal)),"changed_function_was_trusted");
        Require(RecoveryTrustedDumpRestore.CanonicalSchema("SELECT 'first  \nsecond';")!=
            RecoveryTrustedDumpRestore.CanonicalSchema("SELECT 'first\nsecond';"),"quoted_whitespace_was_discarded");
        async Task Reject(string sql,string code)
        {
            try { await RecoveryTrustedDumpRestore.LoadCopyStreamAsync(new StringReader(sql),tables,(_,_)=>Task.FromResult<TextWriter>(new StringWriter()),default); }
            catch(RecoveryWorkerException error) when(error.SafeCode==code) { return; }
            throw new InvalidOperationException("Expected dump data rejection: "+code);
        }
    }
    private static void Require(bool value,string code) { if(!value) throw new InvalidOperationException(code); }
}
