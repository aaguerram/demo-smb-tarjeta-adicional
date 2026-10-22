# proxy-guardar-pdf

API .NET 10 que guarda y lee PDF en una carpeta compartida de Windows por **SMB 3** con la
librería [SMBLibrary](https://www.nuget.org/packages/SMBLibrary) 1.5.8.1. Es el único servicio que
conoce la cuenta de Windows: los demás servicios le envían el PDF por HTTP y no saben nada de SMB.

## Qué hace

- **Recibe el PDF en binario** (`PUT /v1/pdfs/{id}` con `Content-Type: application/pdf`) y lo copia
  por bloques de 64 KB directo a SMB, sin cargarlo completo en memoria.
- **Verifica la integridad**: calcula el SHA-256 mientras copia y lo compara con la cabecera
  `Content-Digest` (RFC 9530), si viene.
- **Escritura atómica**: escribe `{id}.pdf.{guid}.tmp` y lo renombra a `{id}.pdf` solo si todo salió
  bien. Si algo falla, borra el temporal. Quien lea la carpeta nunca ve un PDF a medias.
- **Reutiliza conexiones**: mantiene un pool acotado de conexiones SMB autenticadas (ver
  [Pool de conexiones](#pool-de-conexiones)).
- **Lee el PDF** (`GET /v1/pdfs/{id}`) por bloques, con `Content-Length`.
- **No se queda esperando a Windows**: abrir una conexión y cada respuesta SMB tienen tiempo máximo,
  y un *circuit breaker* corta los intentos de conexión cuando Windows falla de forma sostenida (ver
  [Timeouts y circuit breaker](#timeouts-y-circuit-breaker)).

## Variables de entorno

| Variable | Obligatoria | Por defecto | Ejemplo | Descripción |
|---|---|---|---|---|
| `SMB_SERVIDOR` | Sí | | `10.20.30.40`, `windows` | Nombre o IP del servidor Windows. Se conecta al puerto 445/TCP |
| `SMB_RECURSO` | Sí | | `TarjetaAdicional` | Nombre del recurso compartido (lo que va después de `\\servidor\`) |
| `SMB_USUARIO` | Sí | | `svc-pdf-demo` | Cuenta con permiso de *Modificar* sobre la carpeta (crear, escribir, renombrar y borrar) |
| `SMB_CLAVE` | Sí | | | Clave de la cuenta. Debe venir de un `Secret`, nunca del código ni de la imagen |
| `SMB_DOMINIO` | No | vacío | `DEMO` | Dominio de la cuenta. Vacío para una cuenta local. Una cuenta local también acepta cualquier dominio |
| `SMB_MAX_CONEXIONES` | No | `8` | `4` | Máximo de conexiones SMB por réplica. Las peticiones sin cupo esperan sin ocupar un hilo. Debe ser 1 o más |
| `SMB_INACTIVIDAD_SEGUNDOS` | No | `30` | `60` | Segundos sin uso tras los que se cierra una conexión. En reposo quedan 0 conexiones |
| `SMB_VIDA_MAXIMA_MINUTOS` | No | `10` | `30` | Minutos tras los que una conexión se cierra y se abre una nueva |
| `SMB_TIMEOUT_CONEXION_SEGUNDOS` | No | `3` | `5` | Tiempo máximo para abrir una conexión: DNS, conexión TCP al 445, login y `TreeConnect`. De 1 a 120 |
| `SMB_TIMEOUT_OPERACION_SEGUNDOS` | No | `5` | `10` | Espera máxima de cada respuesta SMB (crear, escribir, leer, renombrar). De 1 a 300 |
| `SMB_CB_PROPORCION_FALLAS` | No | `0.5` | `0.3` | Proporción de aperturas de conexión fallidas que abre el circuito. De 0.01 a 1 |
| `SMB_CB_MINIMO_INTENTOS` | No | `5` | `10` | Mínimo de aperturas de conexión dentro de la ventana para poder abrir el circuito. De 2 a 10000 |
| `SMB_CB_VENTANA_SEGUNDOS` | No | `30` | `60` | Ventana en la que se cuentan las fallas. De 1 a 3600 |
| `SMB_CB_DURACION_ABIERTO_SEGUNDOS` | No | `15` | `60` | Segundos que el circuito queda abierto antes de probar una conexión. Es también el `Retry-After` del `503`. De 1 a 3600 |
| `DIAGNOSTICO_GC` | No | `false` | `true` | Habilita `POST /diagnostico/gc` para pruebas de fuga de memoria. No activar en producción |
| `ASPNETCORE_HTTP_PORTS` | No | `8080` (fijado en el `Dockerfile`) | `8080` | Puerto HTTP de la API |
| `Logging__LogLevel__Default` | No | `Information` | `Warning` | Nivel de log de ASP.NET Core |

Si falta una variable obligatoria, o una numérica no es un número o está fuera de su rango, el
proceso no arranca: escribe en stderr todos los problemas y termina con código 1.

```text
Falta la variable de entorno SMB_RECURSO
Falta la variable de entorno SMB_CLAVE
SMB_CB_PROPORCION_FALLAS debe ser un número entre 0.01 y 1 (se recibió "50")
```

Tampoco arranca si la versión de SMBLibrary no permite fijar `SMB_TIMEOUT_OPERACION_SEGUNDOS` (ver
[Timeouts y circuit breaker](#timeouts-y-circuit-breaker)).

Límites fijos en el código:

| Límite | Valor |
|---|---|
| Tamaño máximo del PDF | 20 MB (`413` si se pasa, antes de abrir una conexión SMB) |
| Bloque de lectura y escritura SMB | 64 KB, por debajo del umbral de 85 KB del *Large Object Heap* de .NET |
| Identificador `{id}` | De 1 a 64 letras, números o guiones. Se guarda como `{id}.pdf` en la raíz del recurso |

## API

| Operación | Petición | Respuestas |
|---|---|---|
| Guardar o reemplazar | `PUT /v1/pdfs/{id}`, cuerpo `application/pdf`, `Content-Length` obligatorio, `Content-Digest: sha-256=:<base64>:` opcional | `201 Created` con `Location` y `ETag` (nuevo), `200 OK` con `ETag` (reemplazo) |
| Leer | `GET /v1/pdfs/{id}` | `200 OK` con `Content-Type: application/pdf` y `Content-Length` |
| Salud | `GET /salud` | `200 OK` `{"estado":"ok"}` |
| Métricas | `GET /metricas` | Hilos, ThreadPool, descriptores abiertos, memoria (`heapTrasGcMB`, `gen2TrasGcMB`), GC, pool de conexiones, estado del circuito y peticiones recibidas. Pensado para pruebas de carga, no para exponer fuera del clúster |
| Forzar GC | `POST /diagnostico/gc` | Solo con `DIAGNOSTICO_GC=true`. Fuerza un GC completo y compactante y devuelve la memoria que sigue viva (`vivoMB`) y las métricas del proceso |

`PUT` es idempotente: reintentar con el mismo `{id}` reemplaza el mismo archivo y no duplica.

Respuesta del `PUT`:

```json
{"id": "SOL-2026-0001", "bytes": 1544, "sha256": "2203792c...ba91b6", "milisegundos": 18}
```

Errores en `application/problem+json` (RFC 9457):

| Código | Cuándo |
|---|---|
| `400` | Id inválido, `Content-Digest` mal formado o que no coincide, o cuerpo incompleto (el cliente cortó el envío) |
| `411` | Falta `Content-Length` |
| `413` | El PDF pasa de 20 MB |
| `415` | `Content-Type` distinto de `application/pdf` |
| `422` | El contenido no empieza con `%PDF-` o está vacío |
| `404` | El `GET` pide un PDF que no existe |
| `502` | Falla la conexión o una operación SMB (por ejemplo `STATUS_LOGON_FAILURE` si la clave es incorrecta) |
| `503` | Circuito abierto: Windows falló de forma sostenida y no se intenta conectar. Incluye `Retry-After` con `SMB_CB_DURACION_ABIERTO_SEGUNDOS` |
| `504` | Windows no respondió a tiempo: abrir la conexión pasó de `SMB_TIMEOUT_CONEXION_SEGUNDOS` o una respuesta SMB pasó de `SMB_TIMEOUT_OPERACION_SEGUNDOS` (`STATUS_IO_TIMEOUT`) |

Ejemplo:

```bash
curl -i -X PUT http://localhost:8080/v1/pdfs/SOL-2026-0001 \
  -H 'Content-Type: application/pdf' \
  -H "Content-Digest: sha-256=:$(openssl dgst -sha256 -binary sol.pdf | base64 -w0):" \
  --data-binary @sol.pdf
```

## Pool de conexiones

Abrir una conexión SMB cuesta varios viajes de red (TCP, negociación SMB 3.1.1, login NTLMv2,
consulta DFS sobre `IPC$` y `TreeConnect` al recurso). El pool (`PoolConexionesSmb.cs`) lo hace una
vez y reutiliza la conexión.

| Regla | Detalle |
|---|---|
| Máximo | `SMB_MAX_CONEXIONES` por réplica |
| Uso exclusivo | Cada conexión atiende una sola petición a la vez (`SMB2Client` no es seguro entre hilos) |
| Orden LIFO | Se reutiliza la última conexión devuelta, así las sobrantes quedan sin uso y se cierran solas |
| Inactividad y vida máxima | Se cierran tras `SMB_INACTIVIDAD_SEGUNDOS` sin uso o al cumplir `SMB_VIDA_MAXIMA_MINUTOS` |
| Conexión rota | Se descarta. Si era reutilizada y falló al abrir el archivo, se reintenta una vez con una nueva, antes de leer el cuerpo |
| Errores de la petición | Digest incorrecto, contenido inválido o archivo inexistente no rompen la conexión: vuelve al pool |
| Apagado | Al detener el proceso cierra las conexiones libres (`TreeDisconnect`, `Logoff`, cierre TCP) |

Con 3 réplicas y `SMB_MAX_CONEXIONES=8`, el servidor Windows recibe como máximo 24 conexiones.

## Timeouts y circuit breaker

Sin límites, SMBLibrary bloquea un hilo unos 2 minutos si el 445 no contesta, y espera cada
respuesta SMB hasta 5 s fijos. El proxy agrega:

| Mecanismo | Detalle |
|---|---|
| Timeout de conexión | `SMB_TIMEOUT_CONEXION_SEGUNDOS` (3 s) para todo el proceso de abrir la conexión: resolución DNS, sonda TCP asíncrona al 445 (no ocupa un hilo mientras espera), login NTLMv2 y `TreeConnect`. Si se pasa, responde `504` |
| Timeout de operación | `SMB_TIMEOUT_OPERACION_SEGUNDOS` (5 s) para cada respuesta SMB. SMBLibrary 1.5.8.1 no lo expone: se fija en el campo privado `m_responseTimeoutInMilliseconds`. Al arrancar se verifica que el campo exista; si una versión nueva de la librería lo quita, el proxy no arranca en lugar de quedar sin timeout |
| Conexión con timeout | `STATUS_IO_TIMEOUT` descarta la conexión (la respuesta podría llegar tarde) sin el cierre ordenado, que esperaría otras respuestas que no llegan. Responde `504` |
| Circuit breaker | Polly.Core 8.4.2 alrededor de **abrir conexiones** (no de usar una ya abierta). Se abre si falla al menos `SMB_CB_PROPORCION_FALLAS` (50 %) de al menos `SMB_CB_MINIMO_INTENTOS` (5) aperturas en `SMB_CB_VENTANA_SEGUNDOS` (30 s). Abierto, responde `503` al instante con `Retry-After`. Pasados `SMB_CB_DURACION_ABIERTO_SEGUNDOS` (15 s) deja pasar una conexión de prueba: si funciona se cierra, si no se vuelve a abrir |
| Qué cuenta como falla | Error de red, error SMB (incluido `STATUS_LOGON_FAILURE`) o timeout al abrir. No cuenta que el cliente cancele su petición |

El circuito es por réplica. Su estado, aperturas, rechazos, fallas y timeouts al abrir se ven en
`/metricas`, en `smb.circuito`.

**Protege la cuenta de Windows.** Un login fallido también abre el circuito. Con una clave
incorrecta, sin circuito cada petición sería un login fallido y la cuenta se bloquearía en segundos
por la política de bloqueo de Windows o del dominio. Con el circuito abierto, cada réplica intenta un
login cada `SMB_CB_DURACION_ABIERTO_SEGUNDOS` (unos 4 por minuto con 15 s). Eso reduce el riesgo pero
no lo elimina: si el umbral de bloqueo es bajo, hay que corregir la clave pronto o subir la duración.

**Relación con el timeout del sd.** Una petición al proxy puede tardar hasta
`SMB_TIMEOUT_CONEXION_SEGUNDOS` + `SMB_TIMEOUT_OPERACION_SEGUNDOS` (8 s con los valores por
defecto) antes de responder `504`. Esa suma debe ser **menor** que `PROXY_TIMEOUT_SEGUNDOS` del sd
(10 s): así el sd recibe un `504` claro del proxy en lugar de cortar la petición por su cuenta sin
saber qué pasó en Windows.

Medido en el laboratorio:

| Escenario | Resultado |
|---|---|
| `SMB_SERVIDOR` inalcanzable (`10.255.255.1`) | 5 respuestas `504` de 3 s cada una (timeout de conexión), se abre el circuito y luego `503` en 1 ms con `Retry-After: 15` |
| Windows congelado (`docker pause`) con conexiones abiertas | La petición en curso responde `504` en 8 s; las siguientes 3 × `504` en 3 s; luego `503` en 1 ms. Al descongelar, el circuito se cierra solo con la conexión de prueba |
| 100 TPS durante 5 min, 1 réplica, 4 corridas | 30.000 peticiones cada una, 0 errores, p95 16 a 17,5 ms, p99 63 a 66 ms. El circuito nunca se abrió |
| Fuga de memoria (`carga/fuga.sh`) | Memoria viva tras GC forzado al calentar, tras 5 min y tras 10 min a 100 TPS: 18,15, 18,18 y 17,79 MB. Sin fuga. Hilos (39 a 41), descriptores (242 a 250) y conexiones al 445 (0 en reposo) vuelven a la línea base |

## Requisitos del lado de Windows

| Requisito | Detalle |
|---|---|
| Protocolo | SMB 3.x en 445/TCP. SMB 1 puede (y debe) estar apagado |
| Cifrado y firma | Compatible con `EncryptData` en el recurso, `RequireSecuritySignature` y `RejectUnencryptedAccess` en el servidor. SMBLibrary cifra con AES-128-CCM |
| Autenticación | NTLMv2. SMBLibrary no soporta Kerberos |
| Permisos | La cuenta necesita *Cambiar* en el recurso compartido y *Modificar* en la carpeta NTFS (el renombre y el borrado del temporal requieren borrar) |
| Red | El 445 abierto desde la IP de salida de los pods |

## Ejecutar

Con el resto del demo, desde la raíz del repositorio: `docker compose up -d --build`.

Solo el proxy:

```bash
docker build -t proxy-guardar-pdf .
docker run --rm -p 8080:8080 \
  -e SMB_SERVIDOR=192.168.1.50 \
  -e SMB_RECURSO=TarjetaAdicional \
  -e SMB_DOMINIO=DEMO \
  -e SMB_USUARIO=svc-pdf-demo \
  -e SMB_CLAVE='***' \
  proxy-guardar-pdf
```

La imagen se compila con el SDK .NET 10 de Red Hat (`ubi9/dotnet-100`) y corre sobre
`ubi9/dotnet-100-aspnet`. Funciona con UID arbitrario y sistema de archivos de solo lectura (solo
necesita `/tmp` escribible), como la SCC `restricted-v2` de OpenShift.

## OpenShift

```yaml
env:
  - name: SMB_SERVIDOR
    value: "10.20.30.40"
  - name: SMB_RECURSO
    value: TarjetaAdicional
  - name: SMB_DOMINIO
    valueFrom: { secretKeyRef: { name: smb-proxy-pdf, key: dominio } }
  - name: SMB_USUARIO
    valueFrom: { secretKeyRef: { name: smb-proxy-pdf, key: usuario } }
  - name: SMB_CLAVE
    valueFrom: { secretKeyRef: { name: smb-proxy-pdf, key: clave } }
  - name: SMB_MAX_CONEXIONES
    value: "8"
  - name: SMB_TIMEOUT_CONEXION_SEGUNDOS
    value: "3"
  - name: SMB_TIMEOUT_OPERACION_SEGUNDOS
    value: "5"
readinessProbe:
  httpGet: { path: /salud, port: 8080 }
livenessProbe:
  httpGet: { path: /salud, port: 8080 }
resources:
  requests: { cpu: 250m, memory: 256Mi }
  limits: { cpu: "2", memory: 512Mi }
```

Conviene definir `livenessProbe`: dentro de un contenedor el proceso es PID 1, y una excepción no
controlada puede dejarlo colgado en lugar de terminar. La sonda hace que el clúster lo reinicie.

## Archivos

| Archivo | Contenido |
|---|---|
| `Program.cs` | Configuración, validación de variables, endpoints y mapeo de errores HTTP |
| `CarpetaPdf.cs` | Guardar (streaming, SHA-256, temporal y renombre) y leer PDF usando el pool |
| `PoolConexionesSmb.cs` | Pool de conexiones SMB (`PoolConexionesSmb`) con circuit breaker, y conexión reutilizable con timeouts (`ConexionSmb`) |
| `MetricasProceso.cs` | Hilos, ThreadPool, descriptores, memoria y GC para `/metricas` |
| `Dockerfile` | Compilación y ejecución sobre imágenes Red Hat UBI 9 |
