using System.Text.Json;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// ─── Load local secrets (gitignored) ─────────────────────────────────────────
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);

// ─── Azure Key Vault (feature flag) ──────────────────────────────────────────
// Set "UseAzureKeyVault": true in appsettings.json to fetch secrets from vault.
// When false, falls back to appsettings.Local.json connection string.
var useKeyVault = builder.Configuration.GetValue<bool>("UseAzureKeyVault");
string connStr;

if (useKeyVault)
{
    var keyVaultUrl  = builder.Configuration["AzureKeyVault:Url"]
        ?? throw new InvalidOperationException("Missing AzureKeyVault:Url in appsettings.json");
    var secretName   = builder.Configuration["AzureKeyVault:SecretName"]
        ?? throw new InvalidOperationException("Missing AzureKeyVault:SecretName in appsettings.json");

    var client = new SecretClient(new Uri(keyVaultUrl), new DefaultAzureCredential());
    var secret = await client.GetSecretAsync(secretName);
    connStr    = secret.Value.Value;
}
else
{
    connStr = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException(
               "Missing ConnectionStrings:Postgres in appsettings.Local.json");
}

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

    try
    {
        // Step 1: CALL — cursor named explicitly so no need to read it back
        await using var callCmd = new NpgsqlCommand(
            "CALL digital.p_getopvitals_growthchart(@p_uhid, 'mycursor'::refcursor)", conn, tx);
        callCmd.Parameters.AddWithValue("p_uhid", opnumber.Trim());
        await callCmd.ExecuteNonQueryAsync();

        // Step 2: FETCH ALL from the named cursor
        await using var fetchCmd = new NpgsqlCommand(
            "FETCH ALL FROM \"mycursor\"", conn, tx);
        await using var reader = await fetchCmd.ExecuteReaderAsync();

        DateTime birth        = default;
        string   gender       = "male";
        bool     hasRow       = false;
        string   patientOpNum = "";
        double?  fatherHeight = null;
        double?  motherHeight = null;

        var weightData = new List<VitalReading>();
        var lengthData = new List<VitalReading>();
        var headCData  = new List<VitalReading>();
        var bmiData    = new List<VitalReading>();

        while (await reader.ReadAsync())
        {
            if (!hasRow)
            {
                hasRow       = true;
                patientOpNum = SafeString(reader, "opnumber") ?? "";
                birth        = SafeDate(reader, "dob");

                var raw = (SafeString(reader, "gender") ?? "").Trim().ToLower();
                gender  = raw switch
                {
                    "f" or "female" => "female",
                    _               => "male"
                };
            }

            // visit_date = CURRENT_DATE from proc until real column available
            var visitDate = SafeDateWithFallback(reader, "visit_date", DateTime.Today);
            var agemos    = birth != default ? AgeInMonths(birth, visitDate) : 0.0;

            var ht = SafeDouble(reader, "patientheight");
            var wt = SafeDouble(reader, "patientweight");
            var hc = SafeDouble(reader, "head_circumm");
            var bm = SafeDouble(reader, "bmi");

            // Read parent heights inside loop while reader is open
            fatherHeight = SafeDouble(reader, "father_height");
            motherHeight = SafeDouble(reader, "mother_height");

            if (wt.HasValue)
                weightData.Add(new VitalReading { Agemos = agemos, Value = wt.Value });
            if (ht.HasValue)
                lengthData.Add(new VitalReading { Agemos = agemos, Value = ht.Value });
            if (hc.HasValue)
                headCData.Add(new VitalReading { Agemos = agemos, Value = hc.Value });

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

        if (!hasRow) return null;

        return new ProcessedPatientData
        {
            Demographics = new Dictionary<string, object>
            {
                { "name",     patientOpNum.Trim() },
                { "opnumber", patientOpNum },
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
            FamilyHistory = new Dictionary<string, ParentData>
            {
                // IsBio = true only when real DB value present
                // Defaults: WHO/CDC population averages (father 170cm, mother 158cm)
                { "father", new() { Height = fatherHeight ?? 170.0, IsBio = fatherHeight.HasValue } },
                { "mother", new() { Height = motherHeight ?? 158.0, IsBio = motherHeight.HasValue } }
            }
        };
    }
    catch
    {
        await tx.RollbackAsync();
        throw;
    }
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

// Debug endpoint removed — returned DB internals here previously; removed for safety.

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