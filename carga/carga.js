// Prueba de carga de 5 minutos del demo:
//   flujo    -> POST al sd: genera el PDF y lo guarda en Windows a través del proxy.
//   grandes  -> PUT directo al proxy con PDF de 256 KB, 1 MB y 4 MB (memoria en la recepción y en SMB).
//   lecturas -> GET de PDF grandes desde la carpeta compartida.
import http from 'k6/http';
import crypto from 'k6/crypto';
import exec from 'k6/execution';
import { check } from 'k6';

const SD = __ENV.SD_URL || 'http://sd-generar-solicitud-tarjeta-pdf:8080';
const PROXY = __ENV.PROXY_URL || 'http://proxy-guardar-pdf:8080';
const TASA_FLUJO = Number(__ENV.TASA_FLUJO || 40);
const TASA_GRANDES = Number(__ENV.TASA_GRANDES || 4);

function pdf(bytes) {
  const datos = new Uint8Array(bytes);
  datos.set([...'%PDF-1.4\n'].map((c) => c.charCodeAt(0)));
  for (let i = 9; i < bytes; i += 4096) datos[i] = (i * 31) & 0xff;
  return { cuerpo: datos.buffer, digest: `sha-256=:${crypto.sha256(datos.buffer, 'base64')}:`, bytes };
}
const PDFS = [pdf(256 * 1024), pdf(1024 * 1024), pdf(4 * 1024 * 1024)];

export const options = {
  discardResponseBodies: false,
  scenarios: {
    flujo: {
      executor: 'ramping-arrival-rate', exec: 'flujo', startRate: 5, timeUnit: '1s',
      preAllocatedVUs: 50, maxVUs: 300,
      stages: [
        { duration: '30s', target: TASA_FLUJO },
        { duration: '4m', target: TASA_FLUJO },
        { duration: '30s', target: 0 },
      ],
    },
    grandes: {
      executor: 'constant-arrival-rate', exec: 'grandes', rate: TASA_GRANDES, timeUnit: '1s',
      duration: '5m', preAllocatedVUs: 20, maxVUs: 100,
    },
    lecturas: {
      executor: 'constant-arrival-rate', exec: 'lecturas', rate: 2, timeUnit: '1s',
      duration: '5m', startTime: '10s', preAllocatedVUs: 10, maxVUs: 50,
    },
  },
  thresholds: {
    'http_req_failed': ['rate<0.01'],
    'http_req_duration{escenario:flujo}': ['p(95)<1000'],
    'http_req_duration{escenario:grandes}': ['p(95)<3000'],
    'checks': ['rate>0.99'],
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

export function flujo() {
  const id = `CARGA-${exec.scenario.iterationInTest}`;
  const r = http.post(`${SD}/v1/solicitudes-tarjeta-adicional/${id}/pdf`, JSON.stringify({
    titularPrincipal: 'María José Peñafiel', ultimosDigitosTarjetaPrincipal: '4821',
    nombreAdicional: 'Andrés Peñafiel', identificacionAdicional: '1712345678',
    parentesco: 'Hijo', cupoAsignado: 1500,
  }), { headers: { 'Content-Type': 'application/json' }, tags: { escenario: 'flujo' } });
  check(r, { 'flujo 201': (x) => x.status === 201 });
}

export function grandes() {
  const p = PDFS[exec.scenario.iterationInTest % PDFS.length];
  // Un id por VU y tamaño: cada VU reemplaza sus propios archivos (PUT idempotente).
  const id = `CARGA-G-${exec.vu.idInTest}-${p.bytes}`;
  const r = http.put(`${PROXY}/v1/pdfs/${id}`, p.cuerpo, {
    headers: { 'Content-Type': 'application/pdf', 'Content-Digest': p.digest },
    tags: { escenario: 'grandes', tamano: String(p.bytes) },
  });
  check(r, { 'grandes 201/200': (x) => x.status === 201 || x.status === 200 });
}

// Antes de la carga se guarda el PDF de 1 MB que leen las lecturas.
export function setup() {
  const p = PDFS[1];
  const r = http.put(`${PROXY}/v1/pdfs/CARGA-LECTURA`, p.cuerpo,
    { headers: { 'Content-Type': 'application/pdf', 'Content-Digest': p.digest } });
  if (r.status !== 201 && r.status !== 200) throw new Error(`setup: ${r.status} ${r.body}`);
}

export function lecturas() {
  const r = http.get(`${PROXY}/v1/pdfs/CARGA-LECTURA`, { responseType: 'binary', tags: { escenario: 'lecturas' } });
  check(r, {
    'lectura 200': (x) => x.status === 200,
    'lectura íntegra': (x) => x.body && crypto.sha256(x.body, 'base64') === PDFS[1].digest.slice(9, -1),
  });
}
