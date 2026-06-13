import { Component, ChangeDetectorRef } from '@angular/core';
import { environment } from '../environments/environment';

interface PatientSummary {
  opnumber: string;
  name:     string;
  dob:      string;
  gender:   string;
}

@Component({
  selector:    'app-root',
  templateUrl: './app.component.html',
  styleUrls:   ['./app.component.css'],
  standalone:  false
})
export class AppComponent {

  uhid          = '';
  patient:      PatientSummary | null = null;
  lookupMessage = '';
  errorMessage  = '';
  isLooking     = false;
  isLoading     = false;

  heightUnit: 'cm' | 'ft' = 'cm';

  fatherCm: number | null = null;
  motherCm: number | null = null;

  fatherFt: number | null = null;
  fatherIn: number | null = null;
  motherFt: number | null = null;
  motherIn: number | null = null;

  private fatherHeightCm: number | null = null;
  private motherHeightCm: number | null = null;

  mph:     number | null = null;
  mphLow:  number | null = null;
  mphHigh: number | null = null;

  private lookupTimer: any = null;

  constructor(private cdr: ChangeDetectorRef) {}

  onUhidInput(): void {
    this.patient       = null;
    this.lookupMessage = '';
    this.errorMessage  = '';
    this.isLooking     = false;
    this.clearParentalHeights();

    clearTimeout(this.lookupTimer);
    if (!this.uhid.trim()) return;

    this.isLooking = true;
    this.lookupTimer = setTimeout(() => this.lookupUhid(), 500);
  }

  lookupUhid(): void {
    const uhid = this.uhid.trim();
    if (!uhid) return;

    fetch(`${environment.apiUrl}/api/patients/search?opnumber=${encodeURIComponent(uhid)}`)
      .then(r => {
        if (!r.ok) throw new Error(`Server error ${r.status}`);
        return r.json();
      })
      .then((res: any) => {
        this.isLooking = false;

        if (res?.found) {
          this.patient = {
            opnumber: res.opnumber ?? '',
            name:     res.name     ?? '',
            dob:      res.dob      ?? '',
            gender:   res.gender   ?? ''
          };
          if (res.fatherHeight) this.prefillFatherHeight(res.fatherHeight);
          if (res.motherHeight) this.prefillMotherHeight(res.motherHeight);
          this.lookupMessage = 'Patient found - press "Open Growth Chart" to continue.';
        } else {
          this.patient       = null;
          this.lookupMessage = 'No patient found for this OPID.';
        }

        this.cdr.detectChanges();  // force Angular to re-render
      })
      .catch((err: Error) => {
        this.isLooking    = false;
        this.patient      = null;
        this.errorMessage = err.message ?? `Cannot reach API at ${environment.apiUrl}`;
        this.cdr.detectChanges();
      });
  }

  setHeightUnit(unit: 'cm' | 'ft'): void {
    if (this.heightUnit === unit) return;
    this.heightUnit = unit;

    if (unit === 'ft') {
      if (this.fatherHeightCm) {
        [this.fatherFt, this.fatherIn] = this.cmToFtIn(this.fatherHeightCm);
      }
      if (this.motherHeightCm) {
        [this.motherFt, this.motherIn] = this.cmToFtIn(this.motherHeightCm);
      }
    } else {
      if (this.fatherHeightCm) this.fatherCm = Math.round(this.fatherHeightCm);
      if (this.motherHeightCm) this.motherCm = Math.round(this.motherHeightCm);
    }
  }

  onParentHeightChange(): void {
    if (this.heightUnit === 'cm') {
      this.fatherHeightCm = this.fatherCm ?? null;
      this.motherHeightCm = this.motherCm ?? null;
    } else {
      this.fatherHeightCm = this.ftInToCm(this.fatherFt, this.fatherIn);
      this.motherHeightCm = this.ftInToCm(this.motherFt, this.motherIn);
    }
    this.computeMph();
  }

  private computeMph(): void {
    if (!this.fatherHeightCm || !this.motherHeightCm || !this.patient) {
      this.mph = this.mphLow = this.mphHigh = null;
      return;
    }
    const adj    = this.patient.gender.toLowerCase() === 'male' ? 13 : -13;
    this.mph     = (this.fatherHeightCm + this.motherHeightCm + adj) / 2;
    this.mphLow  = this.mph - 8.5;
    this.mphHigh = this.mph + 8.5;
  }

  openChart(): void {
    if (!this.patient) return;
    this.isLoading = true;

    const payload: any = {
      patientId:   this.uhid.trim(),
      patientName: this.patient.name.trim()
    };
    if (this.fatherHeightCm) payload.fatherHeight = +this.fatherHeightCm.toFixed(1);
    if (this.motherHeightCm) payload.motherHeight = +this.motherHeightCm.toFixed(1);

    const token = btoa(JSON.stringify(payload));
    window.location.href = `${environment.chartUrl}/index.html?d=${encodeURIComponent(token)}`;
  }

  formatDob(iso: string): string {
    if (!iso || iso.length !== 10) return iso ?? '-';
    const [y, m, d] = iso.split('-');
    return `${d}/${m}/${y}`;
  }

  private cmToFtIn(cm: number): [number, number] {
    const totalInches = cm / 2.54;
    const ft          = Math.floor(totalInches / 12);
    const inches      = Math.round(totalInches % 12);
    return [ft, inches];
  }

  private ftInToCm(ft: number | null, inches: number | null): number | null {
    if (ft === null && inches === null) return null;
    return ((ft ?? 0) * 12 + (inches ?? 0)) * 2.54;
  }

  private prefillFatherHeight(cm: number): void {
    this.fatherHeightCm = cm;
    if (this.heightUnit === 'cm') {
      this.fatherCm = Math.round(cm);
    } else {
      [this.fatherFt, this.fatherIn] = this.cmToFtIn(cm);
    }
    this.computeMph();
  }

  private prefillMotherHeight(cm: number): void {
    this.motherHeightCm = cm;
    if (this.heightUnit === 'cm') {
      this.motherCm = Math.round(cm);
    } else {
      [this.motherFt, this.motherIn] = this.cmToFtIn(cm);
    }
    this.computeMph();
  }

  private clearParentalHeights(): void {
    this.fatherCm = this.motherCm = null;
    this.fatherFt = this.fatherIn = null;
    this.motherFt = this.motherIn = null;
    this.fatherHeightCm = this.motherHeightCm = null;
    this.mph = this.mphLow = this.mphHigh = null;
  }
}