using System.Collections.Concurrent;
using System.Text.Json;

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

var app = builder.Build();
app.UseCors("dev");

// ─── JSON FILE PERSISTENCE ─────────────────────────────────────────────────────
var storageFile = Path.Combine(AppContext.BaseDirectory, "patients.json");
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};

void SaveStore(ConcurrentDictionary<string, StoredPatient> s)
{
    try { File.WriteAllText(storageFile, JsonSerializer.Serialize(s, jsonOptions)); }
    catch (Exception ex) { Console.WriteLine($"Warning: could not save patients.json: {ex.Message}"); }
}

var store = new ConcurrentDictionary<string, StoredPatient>();
if (File.Exists(storageFile))
{
    try
    {
        var loaded = JsonSerializer.Deserialize<ConcurrentDictionary<string, StoredPatient>>(
            File.ReadAllText(storageFile), jsonOptions);
        if (loaded != null)
            foreach (var kvp in loaded) store[kvp.Key] = kvp.Value;
        Console.WriteLine($"Loaded {store.Count} patient(s) from patients.json");
    }
    catch (Exception ex) { Console.WriteLine($"Warning: could not load patients.json: {ex.Message}"); }
}

// ─── HELPER: age in months from DOB + observation date ────────────────────────
static double AgeInMonths(string dob, string observationDate)
{
    if (!DateTime.TryParse(dob, out var birth) ||
        !DateTime.TryParse(observationDate, out var obs))
        return 0;
    return (obs.Year - birth.Year) * 12 + (obs.Month - birth.Month)
           + (obs.Day - birth.Day) / 30.0;
}

// ─── HELPER: build the processed data the chart expects ───────────────────────
static ProcessedPatientData BuildProcessed(StoredPatient p)
{
    var weightData = p.Observations
        .Where(o => o.Weight.HasValue)
        .Select(o => new VitalReading
        {
            Agemos = AgeInMonths(p.Dob, o.Date),
            Value  = o.Weight!.Value
        }).ToList();

    var lengthData = p.Observations
        .Where(o => o.Height.HasValue)
        .Select(o => new VitalReading
        {
            Agemos = AgeInMonths(p.Dob, o.Date),
            Value  = o.Height!.Value
        }).ToList();

    // BMI = weight(kg) / (height(m))^2
    var bmiData = p.Observations
        .Where(o => o.Weight.HasValue && o.Height.HasValue && o.Height > 0)
        .Select(o => new VitalReading
        {
            Agemos = AgeInMonths(p.Dob, o.Date),
            Value  = Math.Round(o.Weight!.Value / Math.Pow(o.Height!.Value / 100.0, 2), 1)
        }).ToList();

    return new ProcessedPatientData
    {
        Demographics = new Dictionary<string, object>
        {
            { "name",     p.Name },
            { "birthday", p.Dob },
            { "gender",   p.Gender }
        },
        Vitals = new Dictionary<string, List<VitalReading>>
        {
            { "weightData", weightData },
            { "lengthData", lengthData },
            { "headCData",  new() },
            { "BMIData",    bmiData }
        },
        BoneAge       = new(),
        FamilyHistory = new Dictionary<string, ParentData>
        {
            { "father", new() { Height = null, IsBio = false } },
            { "mother", new() { Height = null, IsBio = false } }
        }
    };
}

// ============ Endpoints ============

// POST /api/patients
// If name+dob already exists → append observation. Else → create new patient.
app.MapPost("/api/patients", (PatientFormData form) =>
{
    if (string.IsNullOrWhiteSpace(form.PatientName) || string.IsNullOrWhiteSpace(form.DateOfBirth))
        return Results.BadRequest(new { error = "PatientName and DateOfBirth are required." });

    var matchKey = $"{form.PatientName.Trim().ToLower()}|{form.DateOfBirth.Trim()}";

    var existing = store.Values.FirstOrDefault(p =>
        $"{p.Name.Trim().ToLower()}|{p.Dob.Trim()}" == matchKey);

    var newObs = new Observation
    {
        Date   = form.ObservationDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd"),
        Height = form.Height,
        Weight = form.Weight
    };

    bool isNew;
    StoredPatient patient;

    if (existing != null)
    {
        // Existing patient — append observation
        existing.Observations.Add(newObs);
        patient = existing;
        isNew   = false;
    }
    else
    {
        // New patient
        var id = "patient-" + Guid.NewGuid().ToString("N");
        patient = new StoredPatient
        {
            PatientId    = id,
            Name         = form.PatientName.Trim(),
            Dob          = form.DateOfBirth.Trim(),
            Gender       = form.Gender.Trim().ToLower(),
            Observations = new List<Observation> { newObs }
        };
        store[id] = patient;
        isNew     = true;
    }

    SaveStore(store);

    return Results.Json(new
    {
        id      = patient.PatientId,
        isNew,
        message = isNew
            ? "New patient created."
            : $"Observation added. Total readings: {patient.Observations.Count}"
    });
})
.WithName("CreateOrUpdatePatient");

// GET /api/patients/{id}/data  — returns chart-ready processed data
app.MapGet("/api/patients/{id}/data", (string id) =>
{
    if (!store.TryGetValue(id, out var patient))
        return Results.NotFound(new { error = "Patient not found" });

    return Results.Json(BuildProcessed(patient));
})
.WithName("GetPatientData");

// GET /api/patients/{id}  — returns raw stored patient (all observations)
app.MapGet("/api/patients/{id}", (string id) =>
{
    if (!store.TryGetValue(id, out var patient))
        return Results.NotFound(new { error = "Patient not found" });

    return Results.Json(patient);
})
.WithName("GetPatient");

// GET /api/patients  — list all patients (summary)
app.MapGet("/api/patients", () =>
{
    var list = store.Values.Select(p => new
    {
        id       = p.PatientId,
        name     = p.Name,
        dob      = p.Dob,
        gender   = p.Gender,
        readings = p.Observations.Count
    }).ToList();

    return Results.Json(list);
})
.WithName("ListPatients");

// DELETE /api/patients/{id}
app.MapDelete("/api/patients/{id}", (string id) =>
{
    if (!store.TryRemove(id, out _))
        return Results.NotFound(new { error = "Patient not found" });

    SaveStore(store);
    return Results.Ok(new { message = "Patient deleted." });
})
.WithName("DeletePatient");

// DELETE /api/patients/{id}/observations/{index}  — remove a single reading
app.MapDelete("/api/patients/{id}/observations/{index}", (string id, int index) =>
{
    if (!store.TryGetValue(id, out var patient))
        return Results.NotFound(new { error = "Patient not found" });

    if (index < 0 || index >= patient.Observations.Count)
        return Results.BadRequest(new { error = "Invalid observation index." });

    patient.Observations.RemoveAt(index);
    SaveStore(store);

    return Results.Ok(new { message = $"Observation {index} removed. Remaining: {patient.Observations.Count}" });
})
.WithName("DeleteObservation");

app.Run();

// ============ Models ============

public class StoredPatient
{
    public string            PatientId    { get; set; } = "";
    public string            Name         { get; set; } = "";
    public string            Dob          { get; set; } = "";
    public string            Gender       { get; set; } = "";
    public List<Observation> Observations { get; set; } = new();
}

public class Observation
{
    public string  Date   { get; set; } = "";
    public double? Height { get; set; }   // cm
    public double? Weight { get; set; }   // kg
}

public class PatientFormData
{
    public string  PatientName     { get; set; } = "";
    public string  Gender          { get; set; } = "";
    public string  DateOfBirth     { get; set; } = "";
    public string? ObservationDate { get; set; }   // defaults to today if null
    public double? Height          { get; set; }
    public double? Weight          { get; set; }
}

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