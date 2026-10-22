#!/usr/bin/env bash
# Prueba de fuga de memoria: memoria viva tras GC forzado antes y después de dos cargas iguales
# de 5 min a 100 TPS. Uso, desde la raíz del repositorio: carga/fuga.sh | tee resultados/fuga.log
# Levanta el proxy y el sd con DIAGNOSTICO_GC=true; al terminar los deja con la configuración normal.
set -u
cd "$(dirname "$0")/.."
DIAGNOSTICO_GC=true docker compose up -d proxy-guardar-pdf sd-generar-solicitud-tarjeta-pdf >/dev/null 2>&1
docker restart demo-smb-tarjeta-adicional-balanceador-proxy-1 >/dev/null; sleep 8
M=$(docker compose port --index 1 proxy-guardar-pdf 8080)
k6() { docker run --rm -e TPS=100 -e DURACION=$1 --network demo-smb-tarjeta-adicional_default -v "$PWD/carga":/carga:ro grafana/k6:1.3.0 run /carga/carga-40tps.js 2>&1 | grep -E 'http_req_failed\.\.|http_reqs\.\.|http_req_duration\.\.|dropped_iterations\.\.'; }
medir() {
  echo "== $1"
  curl -s -X POST $M/diagnostico/gc | python3 -c "import json,sys; d=json.load(sys.stdin); p=d['proceso']; print('proxy vivo %.2f MB | hilos %d desc %d WS %.0f MB' % (d['vivoMB'], p['hilosProceso'], p['descriptores'], p['workingSetMB']))"
  curl -s -X POST localhost:8081/diagnostico/gc | python3 -c "import json,sys; d=json.load(sys.stdin); p=d['proceso']; print('sd    vivo %.2f MB | hilos %d desc %d WS %.0f MB' % (d['vivoMB'], p['hilosProceso'], p['descriptores'], p['workingSetMB']))"
  curl -s $M/metricas | python3 -c "import json,sys; s=json.load(sys.stdin)['smb']; print('pool: abiertas', len(s['conexiones']), 'prestadas', s['prestadas'], 'creadas', s['creadas'], 'circuito', s['circuito']['estado'])"
  docker run --rm --network container:demo-smb-tarjeta-adicional-windows-1 --cap-add NET_ADMIN nicolaka/netshoot:v0.13 sh -c "conntrack -L -p tcp --dport 445 2>/dev/null | grep -c ESTABLISHED" | sed 's/^/445 ESTABLISHED: /'
}
echo "-- calentamiento 30 s"; k6 30s; sleep 40; medir "W (tras calentar y 40 s en reposo)"
echo "-- carga 1: 5 min"; k6 5m; sleep 40; medir "A (tras carga 1 y 40 s en reposo)"
echo "-- carga 2: 5 min"; k6 5m; sleep 40; medir "B (tras carga 2 y 40 s en reposo)"
echo "-- errores en logs: $(docker compose logs --since 15m proxy-guardar-pdf sd-generar-solicitud-tarjeta-pdf 2>&1 | grep -c -E 'fail:|crit:')"
# Vuelve a la configuración normal (DIAGNOSTICO_GC=false, sin /diagnostico/gc).
docker compose up -d proxy-guardar-pdf sd-generar-solicitud-tarjeta-pdf >/dev/null 2>&1
docker restart demo-smb-tarjeta-adicional-balanceador-proxy-1 >/dev/null
