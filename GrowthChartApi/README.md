# GrowthChartApi - ASP.NET Core Backend

Backend service that converts form data → FHIR resources → processed patient model for the growth chart application.

## Setup

```powershell
# From the GrowthChartApi folder
dotnet restore
dotnet run
```

Server runs on `http://localhost:5000` and `https://localhost:5001`.

## Endpoints

### POST /api/patients
Accept form data (name, gender, DOB, height, weight) and return patient ID.

**Request:**
```json
{
  "patientName": "John Doe",
  "gender": "male",
  "dateOfBirth": "2020-01-15",
  "height": 75.5,
  "weight": 12.3
}
```

**Response:**
```json
{
  "id": "patient-abc123..."
}
```

### GET /api/patients/{id}/data
Fetch processed patient data (demographics + vitals) for the growth-chart-app.

**Response:**
```json
{
  "demographics": {
    "name": "John Doe",
    "birthday": "2020-01-15",
    "gender": "male"
  },
  "vitals": {
    "weightData": [ { "agemos": 12, "value": 12.3 } ],
    "lengthData": [ { "agemos": 12, "value": 75.5 } ],
    "headCData": [],
    "BMIData": []
  },
  "boneAge": [],
  "familyHistory": {
    "father": { "height": null, "isBio": false },
    "mother": { "height": null, "isBio": false }
  }
}
```

### GET /api/patients/{id}
Fetch raw FHIR bundle (for debugging).

### GET /api/patients
List all patients in memory.

## Flow

1. Angular form (`GrowthChart`) POSTs plain user data
2. Backend converts to FHIR (Patient + Observation resources)
3. Backend processes FHIR and returns simplified JSON
4. Frontend receives patient ID, redirects to legacy app with `?patientId=...`
5. Legacy app fetches `/api/patients/{id}/data` and renders charts
