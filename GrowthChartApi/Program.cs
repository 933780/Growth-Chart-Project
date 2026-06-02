using System.Text.Json;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// ─── Load local secrets (gitignored) ─────────────────────────────────────────
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);

// ─── JSON camelCase ───────────────────────────────────────────────────────────
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

// ─── CORS ─────────────────────────────────────────────────────────────────────
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddCors(o => o.AddPolicy("dev",
    b => b.WithOrigins(allowedOrigins)
          .AllowAnyHeader()
          .AllowAnyMethod()));

// ─── Swagger ──────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title       = "Growth Chart API",
        Version     = "v1",
        Description = "API for fetching patient growth data from digital.p_getopvitals_growthchart"
    });
});

// ─── Postgres ─────────────────────────────────────────────────────────────────
var connStr = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException(
           "Missing ConnectionStrings:Postgres in appsettings.Local.json");

var app = builder.Build();

app.UseCors("dev");
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Growth Chart API v1");
    c.RoutePrefix = "swagger";
});

// ─── HELPER: open a fresh connection ─────────────────────────────────────────
static async Task<NpgsqlConnection> OpenDb(string connStr)
{
    var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();
    return conn;
}

// ─── HELPER: age in months between two dates ─────────────────────────────────
static double AgeInMonths(DateTime birth, DateTime obs)
{
    return (obs.Year  - birth.Year)  * 12
         + (obs.Month - birth.Month)
         + (obs.Day   - birth.Day)   / 30.0;
}

// ─── HELPER: call digital.p_getopvitals_growthchart via REFCURSOR ────────────
//
// Columns returned by the cursor:
//   uhid          TEXT
//   opnumber      TEXT
//   dob           DATE      ← raw birthdate
//   visit_date    DATE      ← CURRENT_DATE placeholder until real column added
//   gender        TEXT      ('Male' / 'Female')
//   patientheight NUMERIC   (cm, nullable)
//   patientweight NUMERIC   (kg, nullable)
//   head_circumm  NUMERIC   (cm, nullable)
//   bmi           NUMERIC   (nullable)
//   bsa           NUMERIC   (nullable)
//   father_height NUMERIC   (NULL placeholder — defaults to 170.0 if absent)
//   mother_height NUMERIC   (NULL placeholder — defaults to 158.0 if absent)
// ─────────────────────────────────────────────────────────────────────────────
static async Task<ProcessedPatientData?> FetchFromDb(NpgsqlConnection conn, string opnumber)
{
    await using var tx = await conn.BeginTransactionAsync();

    // 1️⃣ Call the stored procedure (cursor)
    await using (var cmd = new NpgsqlCommand("CALL digital.p_getopvitals_growthchart(@opno, @ref)", conn, tx))
    {
        cmd.Parameters.AddWithValue("opno", opnumber);
        cmd.Parameters.AddWithValue("ref", "ref1");
        await cmd.ExecuteNonQueryAsync();
    }

    // 2️⃣ Fetch from cursor
    await using var fetch = new NpgsqlCommand("FETCH ALL IN ref1", conn, tx);
    await using var reader = await fetch.ExecuteReaderAsync();

    if (!reader.HasRows)
        return null;

    var data = new ProcessedPatientData();

    // Prepare collections
    var heightList = new List<VitalReading>();
    var weightList = new List<VitalReading>();

    // Default parents height (fallback)
    double fatherHeight = 170.0;
    double motherHeight = 158.0;

    while (await reader.ReadAsync())
    {
        var dob = SafeDate(reader, "dob");
        var visitDate = SafeDateWithFallback(reader, "visit_date", DateTime.UtcNow);

        var ageMonths = (dob != DateTime.MinValue)
            ? AgeInMonths(dob, visitDate)
            : 0;

        var height = SafeDouble(reader, "patientheight");
        var weight = SafeDouble(reader, "patientweight");

        // ✅ Read parent heights (fallback applied)
        fatherHeight = SafeDouble(reader, "father_height") ?? fatherHeight;
        motherHeight = SafeDouble(reader, "mother_height") ?? motherHeight;

        if (height.HasValue)
        {
            heightList.Add(new VitalReading
            {
                Agemos = ageMonths,
                Value = height.Value
            });
        }

        if (weight.HasValue)
        {
            weightList.Add(new VitalReading
            {
                Agemos = ageMonths,
                Value = weight.Value
            });
        }

        // ✅ Demographics (set once)
        if (data.Demographics.Count == 0)
        {
            data.Demographics["name"] = SafeString(reader, "uhid") ?? "";
            data.Demographics["birthday"] = dob;
            data.Demographics["gender"] = SafeString(reader, "gender") ?? "";
        }
    }

    // Assign vitals
    data.Vitals["height"] = heightList;
    data.Vitals["weight"] = weightList;

    // ✅ Assign family history with fallback applied
    data.FamilyHistory["father"] = new ParentData
    {
        Height = fatherHeight,
        IsBio = true
    };

    data.FamilyHistory["mother"] = new ParentData
    {
        Height = motherHeight,
        IsBio = true
    };

    await tx.CommitAsync();

    return data;
}

// ─── Safe reader helpers ──────────────────────────────────────────────────────
static string? SafeString(NpgsqlDataReader r, string col)
{
    try { var o = r.GetOrdinal(col); return r.IsDBNull(o) ? null : r.GetString(o); }
    catch { return null; }
}

static double? SafeDouble(NpgsqlDataReader r, string col)
{
    try { var o = r.GetOrdinal(col); return r.IsDBNull(o) ? null : Convert.ToDouble(r.GetValue(o)); }
    catch { return null; }
}

static DateTime SafeDate(NpgsqlDataReader r, string col)
{
    try
    {
        var o = r.GetOrdinal(col);
        if (r.IsDBNull(o)) return DateTime.MinValue;
        var val = r.GetValue(o);
        return val switch
        {
            DateTime dt => dt,
            DateOnly d  => d.ToDateTime(TimeOnly.MinValue),
            string s    => DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                               System.Globalization.DateTimeStyles.None, out var p)
                               ? p : DateTime.MinValue,
            _           => DateTime.TryParse(val.ToString(),
                               System.Globalization.CultureInfo.InvariantCulture,
                               System.Globalization.DateTimeStyles.None, out var p2)
                               ? p2 : DateTime.MinValue
        };
    }
    catch { return DateTime.MinValue; }
}

static DateTime SafeDateWithFallback(NpgsqlDataReader r, string col, DateTime fallback)
{
    try
    {
        var o = r.GetOrdinal(col);
        if (r.IsDBNull(o)) return fallback;
        var result = r.GetValue(o) switch
        {
            DateTime dt => dt,
            DateOnly d  => d.ToDateTime(TimeOnly.MinValue),
            string   s  => DateTime.TryParse(s, out var p) ? p : fallback,
            _           => fallback
        };
        return result == DateTime.MinValue ? fallback : result;
    }
    catch { return fallback; }
}

// ============ Endpoints ============

app.MapGet("/api/patients/search", async (string? opnumber) =>
{
    if (string.IsNullOrWhiteSpace(opnumber))
        return Results.BadRequest(new { error = "opnumber parameter required." });
    try
    {
        await using var conn = await OpenDb(connStr);
        var data = await FetchFromDb(conn, opnumber);
        if (data == null) return Results.Json(new { found = false });
        return Results.Json(new
        {
            found    = true,
            opnumber,
            uhid     = data.Demographics["name"],
            dob      = data.Demographics["birthday"],
            gender   = data.Demographics["gender"]
        });
    }
    catch (Exception ex) { return Results.Json(new { error = "DB error", details = ex.Message }, statusCode: 500); }
})
.WithName("SearchByOpNumber").WithTags("Patients")
.WithSummary("Search patient by OP Number")
.WithDescription("Pass an OP Number (e.g. OP1615352). Calls digital.p_getopvitals_growthchart and returns basic demographics.")
.Produces(200).Produces(400).Produces(500);

app.MapGet("/api/patients/{opnumber}/data", async (string opnumber) =>
{
    try
    {
        await using var conn = await OpenDb(connStr);
        var data = await FetchFromDb(conn, opnumber);
        if (data == null) return Results.NotFound(new { error = "Patient not found." });
        return Results.Json(data);
    }
    catch (Exception ex) { return Results.Json(new { error = "DB error", details = ex.Message }, statusCode: 500); }
})
.WithName("GetPatientChartData").WithTags("Patients")
.WithSummary("Get chart-ready patient data by OP Number")
.WithDescription("Pass an OP Number (e.g. OP1615352). Returns demographics + vitals formatted for the growth chart renderer.")
.Produces<ProcessedPatientData>(200).Produces(404).Produces(500);

app.MapGet("/api/patients/{opnumber}", async (string opnumber) =>
{
    try
    {
        await using var conn = await OpenDb(connStr);
        var data = await FetchFromDb(conn, opnumber);
        if (data == null) return Results.NotFound(new { error = "Patient not found." });
        return Results.Json(data);
    }
    catch (Exception ex) { return Results.Json(new { error = "DB error", details = ex.Message }, statusCode: 500); }
})
.WithName("GetPatient").WithTags("Patients")
.WithSummary("Get raw patient data by OP Number")
.WithDescription("Pass an OP Number (e.g. OP1615352). Returns the full patient object including demographics, vitals, and family history.")
.Produces<ProcessedPatientData>(200).Produces(404).Produces(500);

app.MapGet("/api/health", async () =>
{
    try
    {
        await using var conn = await OpenDb(connStr);
        await using var cmd  = new NpgsqlCommand("SELECT 1", conn);
        await cmd.ExecuteScalarAsync();
        return Results.Ok(new { status = "ok", db = "connected" });
    }
    catch (Exception ex) { return Results.Json(new { status = "error", details = ex.Message }, statusCode: 500); }
})
.WithName("HealthCheck").WithTags("Health")
.WithSummary("DB health check")
.WithDescription("Runs SELECT 1 against Postgres. Returns 200 if connected.")
.Produces(200).Produces(500);

// GET /api/debug/{opnumber} ⚠️ REMOVE IN PRODUCTION
app.MapGet("/api/debug/{opnumber}", async (string opnumber) =>
{
    var log = new List<string>();
    try
    {
        await using var conn = await OpenDb(connStr);
        log.Add("✅ DB connection OK");

        await using var tx = await conn.BeginTransactionAsync();
        log.Add("✅ Transaction started");

        try
        {
            await using var callCmd = new NpgsqlCommand(
                "CALL digital.p_getopvitals_growthchart(@p_uhid, 'mycursor'::refcursor)", conn, tx);
            callCmd.Parameters.AddWithValue("p_uhid", opnumber.Trim());
            await callCmd.ExecuteNonQueryAsync();
            log.Add("✅ CALL OK — cursor name: 'mycursor'");
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            log.Add($"❌ CALL failed: {ex.Message}");
            return Results.Json(new { steps = log, hint = "Procedure name/schema/param wrong, or no EXECUTE permission." });
        }

        var rows     = new List<Dictionary<string, object?>>();
        var colNames = new List<string>();
        try
        {
            await using var fetchCmd = new NpgsqlCommand("FETCH ALL FROM \"mycursor\"", conn, tx);
            await using var reader   = await fetchCmd.ExecuteReaderAsync();
            for (int i = 0; i < reader.FieldCount; i++) colNames.Add(reader.GetName(i));
            log.Add($"✅ FETCH OK — columns: [{string.Join(", ", colNames)}]");
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
                rows.Add(row);
            }
            log.Add($"✅ Rows: {rows.Count}");
            await reader.CloseAsync();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            log.Add($"❌ FETCH failed: {ex.Message}");
            return Results.Json(new { steps = log, hint = "Cursor fetch failed." });
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
.WithName("DebugOpNumber").WithTags("Debug")
.WithSummary("⚠️ Debug endpoint — REMOVE IN PRODUCTION")
.WithDescription("Pass an OP Number (e.g. OP1615352). Tests the CALL and FETCH steps separately. Shows exact columns returned by the cursor.");

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