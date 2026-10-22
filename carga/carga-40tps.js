// TPS sostenidos (40 por defecto) durante 5 minutos contra el flujo completo:
//   variables: TPS, DURACION (por ejemplo TPS=100 DURACION=1m)
//   k6 -> sd-generar-solicitud-tarjeta-pdf -> balanceador-proxy (nginx) -> 4 réplicas de
//   proxy-guardar-pdf -> \\windows\TarjetaAdicional
// constant-arrival-rate mantiene la tasa aunque el servicio se ponga lento: si no alcanza, k6
// lo reporta en dropped_iterations.
import http from 'k6/http';
import exec from 'k6/execution';
import { check } from 'k6';

const SD = __ENV.SD_URL || 'http://sd-generar-solicitud-tarjeta-pdf:8080';
const TPS = Number(__ENV.TPS || 40);
const DURACION = __ENV.DURACION || '5m';

export const options = {
  scenarios: {
    sostenido: {
      executor: 'constant-arrival-rate', rate: TPS, timeUnit: '1s', duration: DURACION,
      preAllocatedVUs: 20, maxVUs: 200,
    },
  },
  thresholds: {
    http_req_failed: ['rate==0'],
    checks: ['rate==1'],
    dropped_iterations: ['count==0'],
    http_req_duration: ['p(95)<200', 'p(99)<500'],
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

export default function () {
  const id = `TPS40-${exec.scenario.iterationInTest}`;
  const r = http.post(`${SD}/v1/solicitudes-tarjeta-adicional/${id}/pdf`, JSON.stringify({
    titularPrincipal: 'María José Peñafiel', ultimosDigitosTarjetaPrincipal: '4821',
    nombreAdicional: 'Andrés Peñafiel', identificacionAdicional: '1712345678',
    parentesco: 'Hijo', cupoAsignado: 1500,
  }), { headers: { 'Content-Type': 'application/json' } });
  check(r, { '201': (x) => x.status === 201 });
}
