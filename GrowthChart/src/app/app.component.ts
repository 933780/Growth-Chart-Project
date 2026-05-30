import { Component } from '@angular/core';
import { environment } from '../environments/environment';

interface PatientSummary {
  opnumber: string;
  dob:      string;   // ISO yyyy-MM-dd
  gender:   string;
}

@Component({
  selector:    'app-root',
  templateUrl: './app.component.html',
  styleUrls:   ['./app.component.css']
})
export class AppComponent {

  uhid          = '';
  patient:      PatientSummary | null = null;
  lookupMessage = '';
  errorMessage  = '';
  isLooking     = false;   // spinner during debounce fetch
  isLoading     = false;   // spinner when opening chart

  private lookupTimer: any = null;

  // ── Debounced input handler (500 ms) ─────────────────────────────────────────
  onUhidInput(): void {
    this.patient       = null;
    this.lookupMessage = '';
    this.errorMessage  = '';
    this.isLooking     = false;

    clearTimeout(this.lookupTimer);

    if (!this.uhid.trim()) return;

    this.isLooking   = true;
    this.lookupTimer = setTimeout(() => this.lookupUhid(), 500);
  }

  // ── Fetch patient from backend ────────────────────────────────────────────────
  // Calls GET /api/patients/search?uhid=<uhid>
  // Backend calls the PostgreSQL REFCURSOR procedure and returns demographics.
  lookupUhid(): void {
    const uhid = this.uhid.trim();
    if (!uhid) return;

    fetch(`${environment.apiUrl}/api/patients/search?uhid=${encodeURIComponent(uhid)}`)
      .then(r => {
        if (!r.ok) throw new Error(`Server error ${r.status}`);
        return r.json();
      })
      .then((res: any) => {
        this.isLooking = false;

        if (res?.found) {
          this.patient = {
            opnumber: res.opnumber ?? '',
            dob:      res.dob      ?? '',
            gender:   res.gender   ?? ''
          };
          this.lookupMessage = 'Patient found — press "Open Growth Chart" to continue.';
        } else {
          this.patient       = null;
          this.lookupMessage = 'No patient found for this UHID.';
        }
      })
      .catch((err: Error) => {
        this.isLooking    = false;
        this.patient      = null;
        this.errorMessage = err.message ?? `Cannot reach API at ${environment.apiUrl}`;
        console.error(err);
      });
  }

  // ── Navigate to the growth chart ─────────────────────────────────────────────
  // The chart page calls GET /api/patients/{uhid}/data for the full payload.
  openChart(): void {
    if (!this.patient) return;
    this.isLoading = true;
    const encoded  = encodeURIComponent(this.uhid.trim());
    window.location.href = `${environment.chartUrl}/index.html?patientId=${encoded}`;
  }

  // ── Display helper: ISO yyyy-MM-dd → DD/MM/YYYY ───────────────────────────────
  formatDob(iso: string): string {
    if (!iso || iso.length !== 10) return iso ?? '—';
    const [y, m, d] = iso.split('-');
    return `${d}/${m}/${y}`;
  }
}