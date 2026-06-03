# Growth Chart Project

This is a monorepo containing three interconnected projects:

- **GrowthChartApi** — ASP.NET Core backend API
- **GrowthChart** — Modern Angular frontend (patient search form)
- **growth-chart-app** — Legacy standalone web app (chart renderer)

---

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) (v18+)
- PostgreSQL database access

---

## Setup Instructions

### 1. Backend — GrowthChartApi

```bash
cd GrowthChartApi
dotnet run
```

> The API runs on `http://localhost:5000`

Make sure you have a valid `appsettings.json` (or `appsettings.Local.json`) with your PostgreSQL connection string:

```json
{
  "ConnectionStrings": {
    "Postgres": "Host=...;Database=...;Username=...;Password=..."
  },
  "Cors": {
    "AllowedOrigins": ["http://localhost:4200", "http://localhost:9000"]
  }
}
```

---

### 2. Angular Frontend — GrowthChart

```bash
cd GrowthChart
npm install --legacy-peer-deps
npm start
```

> Runs on `http://localhost:4200`

---

### 3. Legacy Chart App — growth-chart-app

```bash
cd growth-chart-app
npx serve . -p 9000
```

> Runs on `http://localhost:9000`

---

## Usage

1. Open `http://localhost:4200`
2. Enter a patient OP number (e.g. `OP100003`)
3. Click **Open Growth Chart**
4. The chart will open at `http://localhost:9000/index.html?patientId=OP100003`

---

## Notes

- Only pediatric patients (under 20 years) are supported by WHO/CDC growth charts
- `appsettings.Local.json` is gitignored — do not commit DB credentials
- The debug endpoint `/api/debug/{opnumber}` should be removed before production deployment
