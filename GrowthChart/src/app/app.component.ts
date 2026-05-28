import { Component } from '@angular/core';
import { environment } from '../environments/environment';

@Component({
  selector: 'app-root',
  templateUrl: './app.component.html',
  styleUrls: ['./app.component.css']
})
export class AppComponent {
  patientName = '';
  dob = '';
  height = '';
  weight = '';
  gender = 'other';
  isLoading = false;
  errorMessage = '';
  observationDate = '';
  successMessage = '';

getAgeDisplay(): string {
  if (!this.dob) return '';
  const birth = new Date(this.dob);
  const reference = this.observationDate ? new Date(this.observationDate) : new Date();
  const totalMonths =
    (reference.getFullYear() - birth.getFullYear()) * 12 +
    (reference.getMonth() - birth.getMonth());
  const y = Math.floor(totalMonths / 12);
  const m = totalMonths % 12;
  return `${y}y ${m}m`;
}

  get maxDate(): string {
    return new Date().toISOString().split('T')[0];
  }

  get minDate(): string {
    const d = new Date();
    d.setFullYear(d.getFullYear() - 20);
    return d.toISOString().split('T')[0];
  }

  onSubmit() {
    if (!this.patientName.trim()) {
      alert('Patient name is required');
      return;
    }
    if (!this.dob) {
      alert('Date of birth is required');
      return;
    }
    if (this.gender === 'other') {
      alert('Please select Male or Female. The growth chart requires a known gender.');
      return;
    }

    this.isLoading = true;
    this.errorMessage = '';

    const payload = {
      patientName: this.patientName,
      gender: this.gender,
      dateOfBirth: this.dob,
      observationDate: this.observationDate || null,
      height: this.height ? parseFloat(this.height) : null,
      weight: this.weight ? parseFloat(this.weight) : null
    };

    // Uses environment config — change apiUrl in environment.ts to point anywhere
    fetch(`${environment.apiUrl}/api/patients`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    })
    .then(r => {
      if (!r.ok) throw new Error(`Server error: ${r.status} ${r.statusText}`);
      return r.json();
    })
    .then(res => {
      this.isLoading = false;
      this.successMessage = res.isNew
        ? 'New patient created. Opening chart...'
        : `Observation added (${res.message}). Opening chart...`;

      setTimeout(() => {
        window.location.href = `${environment.chartUrl}/index.html?patientId=${res.id}`;
      }, 2000);
    })
    .catch(err => {
      this.isLoading = false;
      this.errorMessage = `Failed to submit: ${err.message}. Is the backend running on ${environment.apiUrl}?`;
      console.error('Submit error:', err);
    });
  }
}