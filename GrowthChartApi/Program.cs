using System.Text.Json;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddCors(o => o.AddPolicy("dev",
    b => b.WithOrigins(allowedOrigins)
          .AllowAnyHeader()
          .AllowAnyMethod()));

var connStr = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException(
           "Missing ConnectionStrings:Postgres in appsettings.json");

var app = builder.Build();
app.UseCors("dev");

// ─── HELPER: open a fresh connection ──────────────────────────────────────────
NpgsqlConnection OpenDb()
{
    var conn = new NpgsqlConnection(connStr);
    conn.Open();
    return conn;
}

// ─── HELPER: age in months between two dates ──────────────────────────────────
static double AgeInMonths(DateTime birth, DateTime obs)
{
    return (obs.Year  - birth.Year)  * 12
         + (obs.Month - birth.Month)
         + (obs.Day   - birth.Day)   / 30.0;
}

// ─── HELPER: call digital.p_getopvitals_growthchart via REFCURSOR ─────────────
//
//   CALL digital.p_getopvitals_growthchart(<uhid>, 'ocursor_opvitals');
//
// Columns returned by the cursor (exact DB names):
//   uhid               TEXT
//   opnumber           TEXT
//   dob                DATE   ← patient's date of birth
//   gender             TEXT   ('M' / 'F')
//   patientheight      NUMERIC (cm, nullable)
//   patientweight      NUMERIC (kg, nullable)
//   head_circumference NUMERIC (cm, nullable)
//   bmi                NUMERIC (nullable — DB-calculated, used as-is)
//   bsa                NUMERIC (nullable)
//
// No visit_date column yet → agemos = age at today's date.
// No patient_name column  → Demographics["name"] = uhid as fallback.
// No parental heights     → FamilyHistory omitted / empty.
// ─────────────────────────────────────────────────────────────────────────────
static async Task<ProcessedPatientData?> FetchFromDb(NpgsqlConnection conn, string uhid)
{
    // PostgreSQL REFCURSOR procedures must run inside a transaction.
    using var tx = await conn.BeginTransactionAsync();

    try
    {
        // Step 1: CALL the procedure.
        // The procedure returns the cursor reference as a result row (not a named cursor),
        // so we use ExecuteScalarAsync to read the cursor name back from the result.
        string cursorName;
        using (var callCmd = new NpgsqlCommand(
            "CALL digital.p_getopvitals_growthchart(@p_uhid, NULL)", conn, tx))
        {
            callCmd.Parameters.AddWithValue("p_uhid", uhid.Trim());
            using var callReader = await callCmd.ExecuteReaderAsync();
            if (!await callReader.ReadAsync())
                throw new Exception("CALL returned no rows — procedure may have failed.");
            cursorName = callReader.GetString(0); // cursor name is in column 0
            await callReader.CloseAsync();
        }

        // Step 2: FETCH ALL rows from the cursor returned by the procedure.
        using var fetchCmd = new NpgsqlCommand($"FETCH ALL FROM \"{cursorName}\"", conn, tx);
        using var reader   = await fetchCmd.ExecuteReaderAsync();

        DateTime birth    = default;
        string   gender   = "male";
        bool     hasRow   = false;
        string   opnumber = "";

        var weightData = new List<VitalReading>();
        var lengthData = new List<VitalReading>();
        var headCData  = new List<VitalReading>();
        var bmiData    = new List<VitalReading>();

        while (await reader.ReadAsync())
        {
            hasRow = true;

            // ── Demographics — read once on first row ─────────────────────
            if (!hasRow || birth == default)
            {
                opnumber = SafeString(reader, "opnumber") ?? "";

                var raw = (SafeString(reader, "gender") ?? "").ToLower();
                gender  = raw switch
                {
                    "f" or "female" => "female",
                    _               => "male"
                };

                // proc returns age-in-years string e.g. "27" not a real date
                // workaround: build a synthetic birthdate from years
                var dobRaw = SafeString(reader, "dob") ?? "0";
                if (double.TryParse(dobRaw.Trim(), out var ageYears) && ageYears > 0)
                    birth = DateTime.Today.AddYears(-(int)ageYears);
            }

            // agemos from synthetic birthdate (accurate to ~1 month)
            var agemos = birth != default && birth != DateTime.MinValue
                ? AgeInMonths(birth, DateTime.Today)
                : 0.0;

            // ── Vitals from exact DB column names ─────────────────────────
            var ht = SafeDouble(reader, "patientheight");
            var wt = SafeDouble(reader, "patientweight");
            var hc = SafeDouble(reader, "head_circumference");
            var bm = SafeDouble(reader, "bmi");   // DB-supplied BMI

            if (wt.HasValue)
                weightData.Add(new VitalReading { Agemos = agemos, Value = wt.Value });

            if (ht.HasValue)
                lengthData.Add(new VitalReading { Agemos = agemos, Value = ht.Value });

            if (hc.HasValue)
                headCData.Add(new VitalReading { Agemos = agemos, Value = hc.Value });

            // Prefer DB-supplied BMI; fall back to calculating if absent.
            if (bm.HasValue)
                bmiData.Add(new VitalReading { Agemos = agemos, Value = bm.Value });
            else if (wt.HasValue && ht.HasValue && ht.Value > 0)
                bmiData.Add(new VitalReading
                {
                    Agemos = agemos,
                    Value  = Math.Round(wt.Value / Math.Pow(ht.Value / 100.0, 2), 1)
                });
        }

        await reader.CloseAsync();
        await tx.CommitAsync();

        if (!hasRow) return null; // UHID not found

        return new ProcessedPatientData
        {
            Demographics = new Dictionary<string, object>
            {
                // No patient_name column yet — use uhid as display identifier.
                { "name",     uhid.Trim() },
                { "opnumber", opnumber },
                { "birthday", birth == default ? "" : birth.ToString("yyyy-MM-dd") },
                { "gender",   gender }
            },
            Vitals = new Dictionary<string, List<VitalReading>>
            {
                { "weightData", weightData },
                { "lengthData", lengthData },
                { "headCData",  headCData  },
                { "BMIData",    bmiData    }
            },
            BoneAge       = new(),
            // No parental height columns in DB — send empty placeholders.
            FamilyHistory = new Dictionary<string, ParentData>
            {
                { "father", new() { Height = null, IsBio = false } },
                { "mother", new() { Height = null, IsBio = false } }
            }
        };
    }
    catch
    {
        await tx.RollbackAsync();
        throw;
    }
}

// ─── Safe reader helpers ───────────────────────────────────────────────────────
static string? SafeString(NpgsqlDataReader r, string col)
{
    try { return r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetString(r.GetOrdinal(col)); }
    catch { return null; }
}

static double? SafeDouble(NpgsqlDataReader r, string col)
{
    try
    {
        var ord = r.GetOrdinal(col);
        return r.IsDBNull(ord) ? null : Convert.ToDouble(r.GetValue(ord));
    }
    catch { return null; }
}

static DateTime SafeDate(NpgsqlDataReader r, string col)
{
    try
    {
        var ord = r.GetOrdinal(col);
        if (r.IsDBNull(ord)) return DateTime.MinValue;
        var val = r.GetValue(ord);
        return val switch
        {
            DateTime dt => dt,
            DateOnly d  => d.ToDateTime(TimeOnly.MinValue),
            string   s  => DateTime.TryParse(s, out var p) ? p : DateTime.MinValue,
            _           => DateTime.MinValue
        };
    }
    catch { return DateTime.MinValue; }
}


// ============ Endpoints ============

// GET /api/patients/search?uhid=OP1615352
app.MapGet("/api/patients/search", async (string? uhid) =>
{
    if (string.IsNullOrWhiteSpace(uhid))
        return Results.BadRequest(new { error = "uhid parameter required." });

    try
    {
        using var conn = OpenDb();
        var data = await FetchFromDb(conn, uhid);

        if (data == null)
            return Results.Json(new { found = false });

        return Results.Json(new
        {
            found    = true,
            uhid,
            opnumber = data.Demographics["opnumber"],
            dob      = data.Demographics["birthday"],
            gender   = data.Demographics["gender"]
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = "DB error", details = ex.Message }, statusCode: 500);
    }
})
.WithName("SearchByUhid");

// GET /api/patients/{uhid}/data  — full chart payload
app.MapGet("/api/patients/{uhid}/data", async (string uhid) =>
{
    try
    {
        using var conn = OpenDb();
        var data = await FetchFromDb(conn, uhid);

        if (data == null)
            return Results.NotFound(new { error = "Patient not found." });

        return Results.Json(data);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = "DB error", details = ex.Message }, statusCode: 500);
    }
})
.WithName("GetPatientData");

// GET /api/patients/{uhid}  — alias
app.MapGet("/api/patients/{uhid}", async (string uhid) =>
{
    try
    {
        using var conn = OpenDb();
        var data = await FetchFromDb(conn, uhid);

        if (data == null)
            return Results.NotFound(new { error = "Patient not found." });

        return Results.Json(data);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = "DB error", details = ex.Message }, statusCode: 500);
    }
})
.WithName("GetPatient");

// GET /api/health
app.MapGet("/api/health", async () =>
{
    try
    {
        using var conn = OpenDb();
        using var cmd  = new NpgsqlCommand("SELECT 1", conn);
        await cmd.ExecuteScalarAsync();
        return Results.Ok(new { status = "ok", db = "connected" });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "error", details = ex.Message }, statusCode: 500);
    }
})
.WithName("HealthCheck");

// GET /api/debug/{uhid}
// ⚠️  REMOVE IN PRODUCTION — exposes raw DB errors and query details.
// Hit this first when troubleshooting a 500. It tries the CALL step and the
// FETCH step separately and reports exactly where it fails.
app.MapGet("/api/debug/{uhid}", async (string uhid) =>
{
    var log = new List<string>();
    try
    {
        using var conn = OpenDb();
        log.Add("✅ DB connection OK");

        using var tx = await conn.BeginTransactionAsync();
        log.Add("✅ Transaction started");

        // ── Step 1: CALL ────────────────────────────────────────────────────
        string cursorName;
        try
        {
            using var callCmd = new NpgsqlCommand(
                "CALL digital.p_getopvitals_growthchart(@p_uhid, NULL)",
                conn, tx);
            callCmd.Parameters.AddWithValue("p_uhid", uhid.Trim());
            using var callReader = await callCmd.ExecuteReaderAsync();
            if (!await callReader.ReadAsync())
                throw new Exception("CALL returned no rows.");
            cursorName = callReader.GetString(0);
            await callReader.CloseAsync();
            log.Add($"✅ CALL procedure OK — cursor name: '{cursorName}'");
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            log.Add($"❌ CALL failed: {ex.Message}");
            return Results.Json(new { steps = log, hint = "Procedure name/schema/param wrong, or no EXECUTE permission." });
        }

        // ── Step 2: FETCH ───────────────────────────────────────────────────
        var rows     = new List<Dictionary<string, object?>>();
        var colNames = new List<string>();
        try
        {
            using var fetchCmd = new NpgsqlCommand(
                $"FETCH ALL FROM \"{cursorName}\"", conn, tx);
            using var reader = await fetchCmd.ExecuteReaderAsync();

            for (int i = 0; i < reader.FieldCount; i++)
                colNames.Add(reader.GetName(i));
            log.Add($"✅ FETCH OK — columns: [{string.Join(", ", colNames)}]");

            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
                rows.Add(row);
            }
            log.Add($"✅ Rows returned: {rows.Count}");
            await reader.CloseAsync();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            log.Add($"❌ FETCH failed: {ex.Message}");
            return Results.Json(new { steps = log, hint = "Cursor name mismatch or cursor not opened." });
        }

        await tx.CommitAsync();
        return Results.Json(new { steps = log, columns = colNames, rowCount = rows.Count, firstRow = rows.FirstOrDefault() });
    }
    catch (Exception ex)
    {
        log.Add($"❌ DB connection failed: {ex.Message}");
        return Results.Json(new { steps = log }, statusCode: 500);
    }
})
.WithName("DebugUhid");

app.Run();

// ============ Models ============

public class ProcessedPatientData
{
    public Dictionary<string, object>             Demographics  { get; set; } = new();
    public Dictionary<string, List<VitalReading>> Vitals        { get; set; } = new();
    public List<object>                           BoneAge       { get; set; } = new();
    public Dictionary<string, ParentData>         FamilyHistory { get; set; } = new();
}

public class VitalReading
{
    public double Agemos { get; set; }
    public double Value  { get; set; }
}

public class ParentData
{
    public double? Height { get; set; }
    public bool    IsBio  { get; set; }
}