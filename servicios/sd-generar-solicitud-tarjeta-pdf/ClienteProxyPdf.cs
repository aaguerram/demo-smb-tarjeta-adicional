using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace SdGenerarSolicitudTarjetaPdf;

/// <summary>
/// Cliente de proxy-guardar-pdf. Envía el PDF en binario con PUT /v1/pdfs/{id}:
/// Content-Type application/pdf, Content-Length y Content-Digest (SHA-256, RFC 9530).
/// </summary>
public sealed class ClienteProxyPdf(HttpClient http)
{
    public async Task<PdfGuardado> GuardarAsync(string id, byte[] pdf, CancellationToken ct)
    {
        // ByteArrayContent fija Content-Length y se puede reenviar si el reintento lo necesita.
        var contenido = new ByteArrayContent(pdf);
        contenido.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        contenido.Headers.TryAddWithoutValidation("Content-Digest",
            $"sha-256=:{Convert.ToBase64String(SHA256.HashData(pdf))}:");

        using var respuesta = await http.PutAsync($"v1/pdfs/{Uri.EscapeDataString(id)}", contenido, ct);
        if (!respuesta.IsSuccessStatusCode)
        {
            var problema = await respuesta.Content.ReadAsStringAsync(ct);
            throw new ProxyPdfException((int)respuesta.StatusCode, problema, respuesta.Headers.RetryAfter);
        }

        var cuerpo = await respuesta.Content.ReadFromJsonAsync<RespuestaProxy>(ct)
            ?? throw new ProxyPdfException((int)respuesta.StatusCode, "Respuesta vacía");
        var ubicacion = respuesta.Headers.Location is { } location
            ? new Uri(http.BaseAddress!, location).ToString()
            : new Uri(http.BaseAddress!, $"v1/pdfs/{id}").ToString();
        return new PdfGuardado(ubicacion, (int)respuesta.StatusCode, cuerpo.Bytes, cuerpo.Sha256, cuerpo.Milisegundos);
    }

    private sealed record RespuestaProxy(string Id, long Bytes, string Sha256, long Milisegundos);
}

public sealed record PdfGuardado(string Ubicacion, int EstadoProxy, long Bytes, string Sha256, long MilisegundosProxy);

public sealed class ProxyPdfException(int estado, string detalle, RetryConditionHeaderValue? reintentarDespues = null)
    : Exception($"proxy-guardar-pdf respondió {estado}: {detalle}")
{
    public int Estado { get; } = estado;

    /// <summary>Retry-After del proxy (lo envía con el circuito hacia Windows abierto).</summary>
    public RetryConditionHeaderValue? ReintentarDespues { get; } = reintentarDespues;
}
