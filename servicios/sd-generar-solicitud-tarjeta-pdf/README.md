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
4. **Reintenta si el proxy falla.** Es seguro porque el `PUT` con el id de la solicitud es
   idempotente: un reintento reemplaza el mismo archivo y no lo duplica.
5. **Responde `201`** con la ubicación, el tamaño y el SHA-256 del PDF guardado.

## Variables de entorno

| Variable | Obligatoria | Por defecto | Ejemplo | Descripción |
|---|---|---|---|---|
| `PROXY_GUARDAR_PDF_URL` | Sí | | `http://proxy-guardar-pdf:8080/` | URL base de proxy-guardar-pdf (en OpenShift, el `Service` del proxy). Debe ser `http` o `https` absoluta. Si no termina en `/`, se agrega |
| `ASPNETCORE_HTTP_PORTS` | No | `8080` (fijado en el `Dockerfile`) | `8080` | Puerto HTTP de la API |
| `Logging__LogLevel__Default` | No | `Information` | `Warning` | Nivel de log de ASP.NET Core |

Si `PROXY_GUARDAR_PDF_URL` falta o no es una URL `http(s)` absoluta, el proceso no arranca: escribe
el motivo en stderr y termina con código 1.

```text
Falta la variable de entorno PROXY_GUARDAR_PDF_URL
PROXY_GUARDAR_PDF_URL no es una URL http(s) absoluta: proxy-guardar-pdf:8080
```

No necesita credenciales: la cuenta de Windows solo la tiene el proxy.

## API

| Operación | Petición | Respuestas |
|---|---|---|
| Generar y guardar | `POST /v1/solicitudes-tarjeta-adicional/{id}/pdf` con la solicitud en JSON | `201`, `400`, `502`, `503` |
| Salud | `GET /salud` | `200 OK` `{"estado":"ok"}` |
| Métricas | `GET /metricas` | Hilos, ThreadPool, memoria y GC. Pensado para pruebas de carga, no para exponer fuera del clúster |

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
| `502` | El proxy respondió con error después de los reintentos (por ejemplo, falla SMB con Windows). `detail` incluye la respuesta del proxy |
| `503` | No se pudo hablar con el proxy: sin conexión, sin respuesta a tiempo o circuito abierto |

## Reintentos y tiempos

El cliente HTTP del proxy usa `AddStandardResilienceHandler` de `Microsoft.Extensions.Http.Resilience`
con sus valores por defecto:

| Mecanismo | Valor |
|---|---|
| Reintentos | Hasta 3, con espera exponencial y variación aleatoria, ante `5xx`, `408`, `429` y errores de red |
| Timeout por intento | 10 s |
| Timeout total | 30 s, incluidos los reintentos |
| Circuit breaker | Se abre 5 s si falla el 10 % de al menos 100 peticiones en 30 s |

Con el proxy caído, el sd responde `503` en unos 6 s (después de los reintentos) y se recupera solo
cuando el proxy vuelve. `IHttpClientFactory` renueva las conexiones HTTP cada 2 minutos, así que con
varias réplicas del proxy detrás de un `Service` las peticiones se reparten entre ellas.

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
| `MetricasProceso.cs` | Hilos, ThreadPool, memoria y GC para `/metricas` |
| `Dockerfile` | Compilación y ejecución sobre imágenes Red Hat UBI 9 |
