using System.Globalization;
using System.Text;

namespace SdGenerarSolicitudTarjetaPdf;

/// <summary>
/// Genera un PDF de una página con la fuente Helvetica estándar (no se incrusta). Basta para el
/// demo; en el servicio real se usa la librería de reportes que ya tenga el equipo.
/// </summary>
public static class GeneradorPdf
{
    public static byte[] Generar(SolicitudTarjetaAdicional s)
    {
        var lineas = new (string Etiqueta, string Valor)[]
        {
            ("Número de solicitud", s.Id),
            ("Fecha", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)),
            ("Titular principal", s.TitularPrincipal),
            ("Tarjeta principal", $"**** **** **** {s.UltimosDigitosTarjetaPrincipal}"),
            ("Nombre del adicional", s.NombreAdicional),
            ("Identificación del adicional", s.IdentificacionAdicional),
            ("Parentesco", s.Parentesco),
            ("Cupo asignado", s.CupoAsignado.ToString("N2", CultureInfo.InvariantCulture) + " USD"),
        };

        var contenido = new StringBuilder();
        contenido.Append("BT /F2 16 Tf 50 780 Td (").Append(Escapar("Solicitud de tarjeta de crédito adicional")).Append(") Tj ET\n");
        var y = 740;
        foreach (var (etiqueta, valor) in lineas)
        {
            contenido.Append($"BT /F2 11 Tf 50 {y} Td (").Append(Escapar(etiqueta)).Append(") Tj ET\n");
            contenido.Append($"BT /F1 11 Tf 230 {y} Td (").Append(Escapar(valor)).Append(") Tj ET\n");
            y -= 22;
        }
        var flujo = Encoding.Latin1.GetBytes(contenido.ToString());

        var objetos = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] " +
                "/Resources << /Font << /F1 4 0 R /F2 5 0 R >> >> /Contents 6 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
        };

        using var pdf = new MemoryStream();
        void Escribir(string texto) => pdf.Write(Encoding.Latin1.GetBytes(texto));
        var posiciones = new List<long>();

        Escribir("%PDF-1.4\n%âãÏÓ\n");
        for (var i = 0; i < objetos.Length; i++)
        {
            posiciones.Add(pdf.Position);
            Escribir($"{i + 1} 0 obj\n{objetos[i]}\nendobj\n");
        }
        posiciones.Add(pdf.Position);
        Escribir($"6 0 obj\n<< /Length {flujo.Length} >>\nstream\n");
        pdf.Write(flujo);
        Escribir("endstream\nendobj\n");

        var xref = pdf.Position;
        Escribir($"xref\n0 {posiciones.Count + 1}\n0000000000 65535 f \n");
        foreach (var posicion in posiciones)
            Escribir($"{posicion:D10} 00000 n \n");
        Escribir($"trailer\n<< /Size {posiciones.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }

    // Helvetica con WinAnsiEncoding: los acentos y la ñ coinciden con Latin-1.
    private static string Escapar(string texto) =>
        texto.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
