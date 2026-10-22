// Ráfaga: 100 usuarios virtuales enviando PDF de 4 MB sin pausa durante 60 s.
// Valida que los hilos, la memoria y las conexiones al 445 quedan acotados por SMB_MAX_SESIONES.
import http from 'k6/http';
import crypto from 'k6/crypto';
import exec from 'k6/execution';
import { check } from 'k6';

const PROXY = __ENV.PROXY_URL || 'http://proxy-guardar-pdf:8080';
const datos = new Uint8Array(4 * 1024 * 1024);
datos.set([...'%PDF-1.4\n'].map((c) => c.charCodeAt(0)));
const digest = `sha-256=:${crypto.sha256(datos.buffer, 'base64')}:`;

export const options = {
  scenarios: { rafaga: { executor: 'constant-vus', vus: 100, duration: '60s' } },
  thresholds: { http_req_failed: ['rate<0.01'] },
  summaryTrendStats: ['avg', 'med', 'p(95)', 'p(99)', 'max'],
};

export default function () {
  const r = http.put(`${PROXY}/v1/pdfs/RAFAGA-${exec.vu.idInTest}`, datos.buffer, {
    headers: { 'Content-Type': 'application/pdf', 'Content-Digest': digest }, timeout: '120s',
  });
  check(r, { '201/200': (x) => x.status === 201 || x.status === 200 });
}
