# sd-generar-solicitud-tarjeta-pdf

API .NET 10 que recibe los datos de una solicitud de tarjeta de crédito adicional, genera el PDF y
lo envía a [proxy-guardar-pdf](../proxy-guardar-pdf/README.md) para que lo guarde en la carpeta
compartida de Windows. No sabe nada de SMB ni de Windows: solo conoce la URL del proxy.

## Qué hace

1. **Valida la solicitud.** Revisa los campos obligatorios, que la tarjeta tenga 4 dígitos y que el
   cupo sea mayor a 0. Si algo falla, responde `400` con todos los errores juntos.
2. **Genera el PDF** en memoria, una página con los datos de la solicitud. Las tarjetas se muestran
   enmascaradas (`**** **** **** 4821`).
3. **Envía el PDF al proxy** con `PUT /v1/pdfs/{id}` en binario: `Content-Type: application/pdf`,
   `Content-Length` y `Content-Digest` con el SHA-256 (RFC 9530). Es la forma más eficiente: viajan
   solo los bytes del PDF, sin Base64 (que agrega 33 %) ni `multipart`.
4. **Abre una conexión por petición, con timeout y reintentos.** Sin pool de conexiones, con un
   timeout configurable por intento (`PROXY_TIMEOUT_SEGUNDOS`). Reintentar es seguro porque el `PUT`
   con el id de la solicitud es idempotente: un reintento reemplaza el mismo archivo.
5. **Responde `201`** con la ubicación, el tamaño y el SHA-256 del PDF guardado.

## Variables de entorno

| Variable | Obligatoria | Por defecto | Ejemplo | Descripción |
|---|---|---|---|---|
| `PROXY_GUARDAR_PDF_URL` | Sí | | `http://proxy-guardar-pdf:8080/` | URL base de proxy-guardar-pdf (en OpenShift, el `Service` del proxy). Debe ser `http` o `https` absoluta. Si no termina en `/`, se agrega |
| `PROXY_TIMEOUT_SEGUNDOS` | No | `10` | `5` | Tiempo máximo de cada intento contra el proxy: conexión, envío del PDF y respuesta. De 1 a 300. Debe ser mayor que `SMB_TIMEOUT_CONEXION_SEGUNDOS` + `SMB_TIMEOUT_OPERACION_SEGUNDOS` del proxy (ver [Timeout y reintentos](#conexión-al-proxy)) |
| `PROXY_REINTENTOS` | No | `3` | `0` | Reintentos después del primer intento. `0` los desactiva. De 0 a 10 |
| `PROXY_REINTENTO_ESPERA_MS` | No | `2000` | `500` | Espera base antes del primer reintento. Se duplica en cada reintento, con variación aleatoria. De 0 a 60000 |
| `PROXY_CB_PROPORCION_FALLAS` | No | `0.1` | `0.5` | Proporción de intentos fallidos que abre el circuito. De 0.01 a 1 |
| `PROXY_CB_MINIMO_INTENTOS` | No | `100` | `20` | Mínimo de intentos dentro de la ventana para poder abrir el circuito. De 2 a 100000 |
| `PROXY_CB_VENTANA_SEGUNDOS` | No | 30 o 2 × `PROXY_TIMEOUT_SEGUNDOS`, el mayor | `60` | Ventana en la que se cuentan las fallas. Debe ser al menos 2 × `PROXY_TIMEOUT_SEGUNDOS`. De 1 a 3600 |
| `PROXY_CB_DURACION_ABIERTO_SEGUNDOS` | No | `5` | `30` | Segundos que el circuito queda abierto antes de probar de nuevo. De 1 a 3600 |
| `DIAGNOSTICO_GC` | No | `false` | `true` | Habilita `POST /diagnostico/gc` para pruebas de fuga de memoria. No activar en producción |
| `ASPNETCORE_HTTP_PORTS` | No | `8080` (fijado en el `Dockerfile`) | `8080` | Puerto HTTP de la API |
| `Logging__LogLevel__Default` | No | `Information` | `Warning` | Nivel de log de ASP.NET Core |

Si `PROXY_GUARDAR_PDF_URL` falta o no es una URL `http(s)` absoluta, si una variable numérica no
es un número o está fuera de su rango, o si la ventana del circuito es menor que el doble del
timeout, el proceso no arranca: escribe el motivo en stderr y termina con código 1.

```text
Falta la variable de entorno PROXY_GUARDAR_PDF_URL
PROXY_GUARDAR_PDF_URL no es una URL http(s) absoluta: proxy-guardar-pdf:8080
PROXY_CB_VENTANA_SEGUNDOS (15) debe ser al menos el doble de PROXY_TIMEOUT_SEGUNDOS (10)
```

No necesita credenciales: la cuenta de Windows solo la tiene el proxy.

## API

| Operación | Petición | Respuestas |
|---|---|---|
| Generar y guardar | `POST /v1/solicitudes-tarjeta-adicional/{id}/pdf` con la solicitud en JSON | `201`, `400`, `502`, `503`, `504` |
| Salud | `GET /salud` | `200 OK` `{"estado":"ok"}` |
| Métricas | `GET /metricas` | Hilos, ThreadPool, descriptores abiertos, memoria (`heapTrasGcMB`, `gen2TrasGcMB`) y GC. Pensado para pruebas de carga, no para exponer fuera del clúster |
| Forzar GC | `POST /diagnostico/gc` | Solo con `DIAGNOSTICO_GC=true`. Fuerza un GC completo y compactante y devuelve la memoria que sigue viva (`vivoMB`) y las métricas del proceso |

`{id}`: identificador de la solicitud, de 1 a 64 letras, números o guiones. El PDF se guarda como
`{id}.pdf`.

Cuerpo de la petición:

| Campo | Tipo | Regla |
|---|---|---|
| `titularPrincipal` | texto | Obligatorio, hasta 100 caracteres |
| `ultimosDigitosTarjetaPrincipal` | texto | Exactamente 4 dígitos |
| `nombreAdicional` | texto | Obligatorio, hasta 100 caracteres |
| `identificacionAdicional` | texto | Obligatorio, hasta 100 caracteres |
| `parentesco` | texto | Obligatorio, hasta 100 caracteres |
| `cupoAsignado` | número | Mayor a 0 (USD) |

```bash
curl -i -X POST http://localhost:8081/v1/solicitudes-tarjeta-adicional/SOL-2026-0001/pdf \
  -H 'Content-Type: application/json' \
  -d '{"titularPrincipal":"María José Peñafiel","ultimosDigitosTarjetaPrincipal":"4821",
       "nombreAdicional":"Andrés Peñafiel","identificacionAdicional":"1712345678",
       "parentesco":"Hijo","cupoAsignado":1500}'
```

Respuesta `201` (con `Location` apuntando al PDF en el proxy):

```json
{
  "id": "SOL-2026-0001",
  "pdf": {
    "ubicacion": "http://proxy-guardar-pdf:8080/v1/pdfs/SOL-2026-0001",
    "estadoProxy": 201,
    "bytes": 1544,
    "sha256": "2203792c...ba91b6",
    "milisegundosProxy": 18
  },
  "milisegundos": { "generar": 3, "total": 25 }
}
```

`estadoProxy` es `201` si el PDF es nuevo y `200` si reemplazó uno con el mismo id.

Errores en `application/problem+json` (RFC 9457):

| Código | Cuándo |
|---|---|
| `400` | Id inválido o datos de la solicitud inválidos. `detail` lista todos los errores, por ejemplo `titularPrincipal es obligatorio. cupoAsignado debe ser mayor a 0.` |
| `502` | El proxy respondió con error después de los reintentos (por ejemplo, falla SMB con Windows o clave incorrecta). `detail` incluye la respuesta del proxy |
| `503` | No se pudo conectar con el proxy después de los reintentos, el circuito del sd está abierto, o el circuito del proxy hacia Windows está abierto. En este último caso, sin reintentos y con el `Retry-After` del proxy |
| `504` | El proxy no respondió dentro de `PROXY_TIMEOUT_SEGUNDOS` en ninguno de los intentos, o el proxy respondió `504` en todos porque Windows no respondió a tiempo ("La carpeta compartida no respondió a tiempo") |

## Conexión al proxy

**Sin pool de conexiones.** Cada petición abre su propia conexión HTTP al proxy y se cierra al terminar:
el cliente envía `Connection: close` en todas las peticiones. Así cada petición pasa por el `Service`
y se reparte entre las réplicas del proxy. El cliente se obtiene de `IHttpClientFactory`
(`AddHttpClient<ClienteProxyPdf>`), así que nunca se crea un `HttpClient` por petición.

Costo medido a 40 TPS: p95 de 17,1 ms contra 16,3 ms reutilizando conexiones. Cada petición deja una
conexión en `TIME_WAIT` durante 60 s en el lado que recibe (unas 2.400 a 40 TPS).

**Timeout y reintentos.** `AddStandardResilienceHandler` de `Microsoft.Extensions.Http.Resilience`:

| Mecanismo | Valor |
|---|---|
| Timeout por intento | `PROXY_TIMEOUT_SEGUNDOS` (10 s). `HttpClient.Timeout` queda desactivado |
| Reintentos | `PROXY_REINTENTOS` (3) ante `5xx`, `408`, `429`, errores de red y timeout de un intento. Espera exponencial desde `PROXY_REINTENTO_ESPERA_MS` (2 s, 4 s, 8 s), con variación aleatoria. Cada reintento abre su propia conexión |
| Timeout total | Todos los intentos más las esperas, con 1 s de margen: `PROXY_TIMEOUT_SEGUNDOS` × (`PROXY_REINTENTOS` + 1) + `PROXY_REINTENTO_ESPERA_MS` × (2^`PROXY_REINTENTOS` − 1) + 1 s. Con los valores por defecto, 4 × 10 s + 14 s + 1 s = 55 s |
| Circuit breaker | Se abre `PROXY_CB_DURACION_ABIERTO_SEGUNDOS` (5 s) si falla al menos `PROXY_CB_PROPORCION_FALLAS` (10 %) de al menos `PROXY_CB_MINIMO_INTENTOS` (100) intentos en `PROXY_CB_VENTANA_SEGUNDOS` (30 s o el doble del timeout, lo que sea mayor) |

Reintentar es seguro porque el `PUT` al proxy es idempotente.

**El timeout del sd debe ser mayor que el del proxy.** El proxy puede tardar hasta
`SMB_TIMEOUT_CONEXION_SEGUNDOS` + `SMB_TIMEOUT_OPERACION_SEGUNDOS` (3 s + 5 s = 8 s) en responder
`504` cuando Windows no contesta. Con `PROXY_TIMEOUT_SEGUNDOS` (10 s) por encima de esa suma, el sd
recibe la respuesta del proxy y sabe qué pasó; si fuera menor, cortaría el intento antes y lo
reintentaría mientras el proxy sigue esperando a Windows.

El proxy tiene su propio circuit breaker hacia Windows: cuando está abierto responde `503` al instante
con `Retry-After`. **El sd no reintenta los `503` del proxy**: reintentar no sirve hasta que pase el
`Retry-After`, y el manejador estándar lo respetaría esperando 15 s por reintento (se midió `502` a
los 38 s antes de este cambio). En su lugar responde `503` "Carpeta compartida no disponible" al
instante, con el mismo `Retry-After`. Los demás `5xx`, los timeouts y los errores de red se siguen
reintentando.

Medido:

- Con el balanceador detenido, el sd responde `503` en unos 6 s y se recupera solo.
- Con el balanceador detenido y los valores bajados por variables para la prueba, cada petición hizo
  2 intentos, el circuito se abrió tras 10 intentos fallidos y se cerró solo al volver el balanceador.
- Con el proxy congelado y `PROXY_TIMEOUT_SEGUNDOS=2`, hace 4 intentos de 2 s y responde `504` en 14,3 s.
- Con Windows inalcanzable: mientras el circuito del proxy está cerrado, cada `504` del proxy se
  reintenta (4 intentos, `504` a los 22 s); con el circuito del proxy abierto el sd responde `503` en 3 a 4 ms con `Retry-After: 15`.

## Generación del PDF

`GeneradorPdf.cs` escribe un PDF 1.4 mínimo de una página con las fuentes estándar Helvetica y
Helvetica-Bold (no se incrustan), con codificación WinAnsi: acentos y ñ se ven bien, pero los
caracteres fuera de Latin-1 se reemplazan por `?`. Basta para el demo; en un servicio real se usa la
librería de reportes del equipo, sin cambiar la forma de enviar el PDF al proxy.

## Ejecutar

Con el resto del demo, desde la raíz del repositorio: `docker compose up -d --build`. Queda en
`http://localhost:8081`.

Solo el sd, apuntando a un proxy existente:

```bash
docker build -t sd-generar-solicitud-tarjeta-pdf .
docker run --rm -p 8081:8080 --add-host host.docker.internal:host-gateway \
  -e PROXY_GUARDAR_PDF_URL=http://host.docker.internal:8086/ \
  sd-generar-solicitud-tarjeta-pdf
```

La imagen se compila con el SDK .NET 10 de Red Hat (`ubi9/dotnet-100`) y corre sobre
`ubi9/dotnet-100-aspnet`. Funciona con UID arbitrario y sistema de archivos de solo lectura (solo
necesita `/tmp` escribible), como la SCC `restricted-v2` de OpenShift.

## OpenShift

```yaml
env:
  - name: PROXY_GUARDAR_PDF_URL
    value: http://proxy-guardar-pdf:8080/
  - name: PROXY_TIMEOUT_SEGUNDOS
    value: "10"
readinessProbe:
  httpGet: { path: /salud, port: 8080 }
livenessProbe:
  httpGet: { path: /salud, port: 8080 }
resources:
  requests: { cpu: 100m, memory: 128Mi }
  limits: { cpu: "1", memory: 256Mi }
```

Conviene definir `livenessProbe`: dentro de un contenedor el proceso es PID 1, y una excepción no
controlada puede dejarlo colgado en lugar de terminar. La sonda hace que el clúster lo reinicie.

## Archivos

| Archivo | Contenido |
|---|---|
| `Program.cs` | Configuración, validación de la URL y de la solicitud, endpoint y mapeo de errores |
| `GeneradorPdf.cs` | Generación del PDF en memoria |
| `ClienteProxyPdf.cs` | Envío del PDF al proxy con `PUT` binario, `Content-Length` y `Content-Digest` |
| `MetricasProceso.cs` | Hilos, ThreadPool, descriptores, memoria y GC para `/metricas` |
| `Dockerfile` | Compilación y ejecución sobre imágenes Red Hat UBI 9 |
