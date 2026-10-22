# Demo: PDF de solicitud de tarjeta adicional guardado en una carpeta compartida de Windows

Un microservicio genera el PDF de la solicitud y lo envía a un proxy, que es el único que habla
SMB 3 con Windows y lo guarda en `\\windows\TarjetaAdicional`. Todo corre en contenedores,
incluido Windows, que va como máquina virtual KVM dentro de un contenedor.

```text
cliente ─POST JSON─▶ sd-generar-solicitud-tarjeta-pdf ─PUT application/pdf─▶ balanceador-proxy ─▶ proxy-guardar-pdf ─SMB 3 cifrado─▶ windows
          :8081                                                                  :8086              :8082-8085                     :8006 (consola)
```

| Servicio | Tecnología | Qué hace |
|---|---|---|
| `sd-generar-solicitud-tarjeta-pdf` | .NET 10, imagen Red Hat UBI 9 | `POST /v1/solicitudes-tarjeta-adicional/{id}/pdf`: genera el PDF y lo envía al proxy con `PUT` binario, `Content-Length` y `Content-Digest` (SHA-256). Reintentos con `AddStandardResilienceHandler` |
| `balanceador-proxy` | nginx 1.28 | Reparte cada petición en round-robin entre las réplicas del proxy, sin guardar el PDF en búfer |
| `proxy-guardar-pdf` | .NET 10, SMBLibrary 1.5.8.1, UBI 9 | `PUT` y `GET /v1/pdfs/{id}`. Copia el cuerpo por bloques de 64 KB a un temporal y lo renombra si el SHA-256 coincide. Pool acotado de conexiones SMB |
| `windows` | `dockurr/windows-arm:6.06` | Windows 11 ARM64 con la carpeta compartida `TarjetaAdicional`: SMB 1 apagado, firma obligatoria y cifrado obligatorio |

## Estructura

```text
.
├── docker-compose.yml
├── servicios/
│   ├── sd-generar-solicitud-tarjeta-pdf/   # genera el PDF y llama al proxy (README propio)
│   └── proxy-guardar-pdf/                  # API de PDF + PoolConexionesSmb + CarpetaPdf (README propio)
├── balanceador/nginx.conf                  # round-robin entre réplicas del proxy
├── oem/                                    # scripts que configuran Windows al terminar la instalación
│   ├── install.bat
│   └── configurar-smb.ps1
├── carga/                                  # pruebas de carga (k6) y muestreo de métricas
│   ├── carga-40tps.js
│   ├── carga.js
│   ├── rafaga.js
│   └── muestrear.py
└── windows/                                # disco de la VM de Windows (NO está en git)
```

## Requisitos

| Requisito | Detalle |
|---|---|
| Host Linux con KVM | `/dev/kvm` disponible. En Linux no existen contenedores Windows, por eso Windows corre como VM |
| Arquitectura | El compose usa `dockurr/windows-arm` (host ARM64). En un host **x86_64** cambiar la imagen por `dockurr/windows:6.06` (mismo `VERSION: "11"`) |
| Docker | Docker Engine con Compose v2 |
| Disco | 64 GB aparentes para `windows/` (unos 15 GB reales, el archivo es disperso) |
| Memoria y CPU | 6 GB de RAM y 4 núcleos para la VM, más ~300 MB para los servicios |
| Puertos libres en 127.0.0.1 | 8006 (consola de Windows), 8081 (sd), 8082 a 8085 (réplicas del proxy), 8086 (balanceador) |

No se instala nada en la máquina: los servicios se compilan dentro de las imágenes y k6 corre en un
contenedor.

## La imagen de Windows

### Qué se usó

| Componente | Valor |
|---|---|
| Imagen del contenedor | `dockurr/windows-arm:6.06`, digest `sha256:b4fc524844b078e0aa4cedd4a3e215cfeed64d81ee2243908de31eae38b1e727` |
| Sistema | Windows 11 ARM64 (`VERSION: "11"`, archivo base `win11arm64.iso`) |
| Origen de la ISO | La descarga la propia imagen desde los servidores de Microsoft en el primer arranque |
| Instalación | Desatendida. Al terminar, `oem/install.bat` ejecuta `oem/configurar-smb.ps1` |
| Configuración de SMB | Usuario local `svc-pdf-demo` con clave `Prueba.Smb.2026`, carpeta `C:\TarjetaAdicional` compartida como `TarjetaAdicional` con `EncryptData`, SMB 1 apagado, firma obligatoria, `RejectUnencryptedAccess` y firewall abierto en 445. Registro en `C:\configurar-smb.log` |
| Disco | `windows/data.img` (64 GB disperso) y archivos de la VM (`windows.rom`, `windows.vars`, `windows.mac`, `windows.ver`, `windows.base`, `windows.boot`) |

La carpeta `windows/` está en `.gitignore`: el repositorio solo tiene el código. El disco se
obtiene de una de estas dos formas.

### Opción A: instalar Windows desde cero (descarga automática)

```bash
docker pull dockurr/windows-arm:6.06        # en x86_64: docker pull dockurr/windows:6.06
mkdir -p windows
docker compose up -d windows
docker logs -f demo-smb-tarjeta-adicional-windows-1
```

1. La imagen pide Windows 11 ARM64 a los servidores de Microsoft y descarga la ISO (varios GB)
   dentro de `windows/`.
2. Instala Windows sin intervención. Se puede ver en la consola web `http://127.0.0.1:8006`.
3. Al terminar ejecuta los scripts de `oem/` y queda en el escritorio. Tarda de 30 a 60 minutos
   según la red y el equipo.
4. Al terminar, la imagen borra la ISO y deja `windows/windows.boot`. En los siguientes arranques
   Windows inicia en segundos.

Para usar una ISO propia en lugar de la descarga, montarla como `/boot.iso` en el servicio
`windows` (`- ./mi-windows.iso:/boot.iso`).

Windows queda sin activar: sirve para pruebas, no para producción.

### Opción B: usar el disco ya instalado

Si alguien ya tiene el disco instalado, se comparte comprimido conservando lo disperso. Así no hay
que esperar la instalación.

```bash
# Quien lo tiene (con Windows apagado):
docker compose stop windows
tar --sparse -czf windows-disco.tar.gz windows/

# Quien lo recibe, en la raíz de este repositorio:
tar --sparse -xzf windows-disco.tar.gz      # crea windows/
docker compose up -d windows
```

Para copiarlo desde un volumen de Docker, conservando lo disperso:

```bash
docker run --rm -v <volumen>:/origen:ro -v "$PWD/windows":/destino debian:12-slim \
  sh -c 'cp -a --sparse=always /origen/. /destino/ && chown -R 1000:1000 /destino'
```

## Cómo ejecutar todo

```bash
# 1. Levantar Windows, el proxy, el balanceador y el sd (compila las imágenes .NET).
docker compose up -d --build

# 2. Esperar a que Windows publique el 445 (con el disco ya instalado, unos segundos).
until docker exec demo-smb-tarjeta-adicional-windows-1 nc -z -w2 172.30.64.2 445; do sleep 5; done

# 3. Generar y guardar una solicitud.
curl -i -X POST localhost:8081/v1/solicitudes-tarjeta-adicional/SOL-2026-0001/pdf \
  -H 'Content-Type: application/json' \
  -d '{"titularPrincipal":"María José Peñafiel","ultimosDigitosTarjetaPrincipal":"4821",
       "nombreAdicional":"Andrés Peñafiel","identificacionAdicional":"1712345678",
       "parentesco":"Hijo","cupoAsignado":1500}'

# 4. Leer el PDF desde Windows a través del balanceador.
curl -o sol.pdf localhost:8086/v1/pdfs/SOL-2026-0001

# 5. Enviar un PDF propio directo al proxy (PUT binario con SHA-256).
curl -i -X PUT localhost:8086/v1/pdfs/DEMO-1 -H 'Content-Type: application/pdf' \
  -H "Content-Digest: sha-256=:$(openssl dgst -sha256 -binary sol.pdf | base64 -w0):" \
  --data-binary @sol.pdf

# 6. Apagar. windows/ conserva la instalación.
docker compose down
```

Otras operaciones:

| Para | Comando |
|---|---|
| Ver Windows | Abrir `http://127.0.0.1:8006` |
| Varias réplicas del proxy | `docker compose up -d --scale proxy-guardar-pdf=4` y luego `docker restart demo-smb-tarjeta-adicional-balanceador-proxy-1` |
| Métricas de una réplica (hilos, memoria, pool SMB) | `curl $(docker compose port --index 1 proxy-guardar-pdf 8080)/metricas` |
| Métricas del sd | `curl localhost:8081/metricas` |
| Logs | `docker compose logs -f proxy-guardar-pdf sd-generar-solicitud-tarjeta-pdf` |

## API

### proxy-guardar-pdf

| Operación | Petición | Respuestas |
|---|---|---|
| Guardar o reemplazar | `PUT /v1/pdfs/{id}`, cuerpo `application/pdf`, `Content-Length` obligatorio, `Content-Digest: sha-256=:<base64>:` opcional | `201` nuevo con `Location` y `ETag`, `200` reemplazo, `400` id o digest inválido o cuerpo incompleto, `411` sin `Content-Length`, `413` más de 20 MB, `415` no es `application/pdf`, `422` no empieza con `%PDF-`, `502` falla SMB |
| Leer | `GET /v1/pdfs/{id}` | `200` con `Content-Length`, `404`, `502` |

Errores en `application/problem+json` (RFC 9457). `{id}`: de 1 a 64 letras, números o guiones.

### sd-generar-solicitud-tarjeta-pdf

| Operación | Petición | Respuestas |
|---|---|---|
| Generar y guardar | `POST /v1/solicitudes-tarjeta-adicional/{id}/pdf` con `titularPrincipal`, `ultimosDigitosTarjetaPrincipal`, `nombreAdicional`, `identificacionAdicional`, `parentesco` y `cupoAsignado` | `201` con ubicación, tamaño y SHA-256 del PDF, `400` con todos los errores de validación, `502` si el proxy lo rechazó, `503` si el proxy no responde (sin conexión, timeout o circuito abierto) |

## Pool de conexiones SMB

| Regla | Valor |
|---|---|
| Máximo por réplica | `SMB_MAX_CONEXIONES` (8). Sin cupo, la petición espera sin ocupar un hilo |
| Uso | Cada conexión atiende una petición a la vez y se devuelve al pool al terminar |
| Orden | LIFO: se reutiliza la última devuelta y las sobrantes se cierran solas |
| Inactividad | `SMB_INACTIVIDAD_SEGUNDOS` (30). En reposo quedan 0 conexiones |
| Vida máxima | `SMB_VIDA_MAXIMA_MINUTOS` (10) |
| Conexión rota | Se descarta y se reintenta una vez con una nueva, antes de leer el cuerpo |
| Apagado | Cierra todas las conexiones libres (`TreeDisconnect`, `Logoff`, cierre TCP) |

## Configuración

Cada servicio tiene su README con la descripción, la API y todas sus variables de entorno:

- [servicios/proxy-guardar-pdf/README.md](servicios/proxy-guardar-pdf/README.md)
- [servicios/sd-generar-solicitud-tarjeta-pdf/README.md](servicios/sd-generar-solicitud-tarjeta-pdf/README.md)

| Variable | Por defecto | Servicio |
|---|---|---|
| `SMB_DOMINIO` | `DEMO` | proxy. Opcional: vacío para una cuenta local, que también acepta cualquier dominio |
| `SMB_USUARIO` / `SMB_CLAVE` | `svc-pdf-demo` / `Prueba.Smb.2026` | proxy. Deben coincidir con `oem/configurar-smb.ps1` |
| `SMB_MAX_CONEXIONES` | 8 | proxy |
| `SMB_INACTIVIDAD_SEGUNDOS` | 30 | proxy |
| `SMB_VIDA_MAXIMA_MINUTOS` | 10 | proxy |

Se pueden cambiar con variables de entorno o un archivo `.env` (ignorado en git). La clave es solo
del laboratorio.

## Pruebas de carga

k6 corre en un contenedor dentro de la red del demo. `muestrear.py` (Python 3, solo librería
estándar) toma cada 5 s las métricas de cada réplica, la memoria y CPU de los contenedores y las
conexiones al 445 de cada réplica con `conntrack` desde un contenedor `nicolaka/netshoot`.

```bash
mkdir -p resultados
carga/muestrear.py resultados/muestras.jsonl 390 &

# 40 TPS durante 5 minutos (variables: TPS, DURACION)
docker run --rm --network demo-smb-tarjeta-adicional_default -v "$PWD/carga":/carga:ro \
  grafana/k6:1.3.0 run /carga/carga-40tps.js

# 100 TPS durante 1 minuto
docker run --rm -e TPS=100 -e DURACION=1m --network demo-smb-tarjeta-adicional_default \
  -v "$PWD/carga":/carga:ro grafana/k6:1.3.0 run /carga/carga-40tps.js

# Mezcla de 5 minutos (flujo + PDF de 256 KB a 4 MB + lecturas) y ráfaga de 100 usuarios con 4 MB
docker run --rm --network demo-smb-tarjeta-adicional_default -v "$PWD/carga":/carga:ro grafana/k6:1.3.0 run /carga/carga.js
docker run --rm --network demo-smb-tarjeta-adicional_default -v "$PWD/carga":/carga:ro grafana/k6:1.3.0 run /carga/rafaga.js
```

Resultados obtenidos en el laboratorio (host ARM64, Windows 11 en VM):

| Prueba | Resultado |
|---|---|
| 40 TPS, 4 réplicas, 5 min | 12.001 peticiones, 0 errores, p95 14,7 ms. 4 a 5 conexiones SMB en total, 0 esperas |
| 40 TPS, 1 réplica, 5 min | 0 errores, p95 14,1 ms. 5 conexiones creadas y 11.996 reutilizadas, máximo 4 en uso a la vez |
| 100 TPS, 1 réplica, 5 min | 30.001 peticiones, 0 errores, p95 16,8 ms, p99 56,5 ms. Llega al límite de 8 conexiones en picos: 0,11 % esperó hasta 75 ms |
| Ráfaga, 100 usuarios con PDF de 4 MB | 0 errores, 516 MB/s, nunca más de 8 conexiones |
| Hilos y memoria | Cola del ThreadPool en 0 o 1, 40 a 70 MB por réplica, sin errores en los logs |

## Problemas comunes

| Síntoma | Causa y solución |
|---|---|
| El cliente no llega al 445 o recibe "connection refused" | Windows todavía arranca o instala. Esperar el paso 2 o mirar la consola en `:8006` |
| `/dev/kvm` no existe | Activar la virtualización en el BIOS o usar un host con KVM |
| `exec format error` o la VM no arranca en x86_64 | Cambiar la imagen por `dockurr/windows:6.06` |
| El puerto 8006, 8081 u 8086 está ocupado | Cambiar el puerto del host en `docker-compose.yml` |
| `502` del proxy | Revisar `docker compose logs proxy-guardar-pdf`. Con `STATUS_LOGON_FAILURE`, la clave no coincide con `oem/configurar-smb.ps1` |
| El balanceador no ve réplicas nuevas | `docker restart demo-smb-tarjeta-adicional-balanceador-proxy-1` |
