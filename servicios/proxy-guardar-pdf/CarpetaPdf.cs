using System.Buffers;
using System.Security.Cryptography;
using SMBLibrary;
using FileAttributes = SMBLibrary.FileAttributes;

namespace ProxyGuardarPdf;

/// <summary>
/// Guarda y lee PDF en la carpeta compartida de Windows por SMB 3 con SMBLibrary, usando las
/// conexiones de <see cref="PoolConexionesSmb"/>. El cuerpo HTTP se copia por bloques directo a
/// SMB, sin cargar el PDF completo en memoria.
/// </summary>
public sealed class CarpetaPdf(PoolConexionesSmb pool)
{
    // Bloques de 64 KB: quedan bajo el umbral de 85 KB del Large Object Heap. Con el máximo que
    // ofrece Windows (1 MB), cada bloque y las copias internas de SMBLibrary caen en el LOH y
    // provocan recolecciones de generación 2.
    private const int BloqueSmb = 64 * 1024;

    private static readonly byte[] FirmaPdf = "%PDF-"u8.ToArray();

    /// <summary>
    /// Escribe primero en un archivo temporal y lo renombra al nombre final solo si el contenido
    /// es un PDF y su SHA-256 coincide. Quien lea la carpeta nunca ve un PDF a medias.
    /// </summary>
    public async Task<ResultadoGuardar> GuardarAsync(
        string nombre, Stream origen, byte[]? sha256Esperado, CancellationToken ct)
    {
        var temporal = $"{nombre}.{Guid.NewGuid():N}.tmp";
        var (conexion, archivo) = await AbrirArchivoAsync(temporal,
            AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE,
            ShareAccess.None, CreateDisposition.FILE_CREATE, ct);
        var recurso = conexion.Recurso!;
        var renombrado = false;
        var bloque = ArrayPool<byte>.Shared.Rent(BloqueSmb);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var tamanoBloque = (int)Math.Min(BloqueSmb, conexion.Cliente.MaxWriteSize);
            long offset = 0;
            int leidos;
            while ((leidos = await origen.ReadAtLeastAsync(bloque.AsMemory(0, tamanoBloque), tamanoBloque, false, ct)) > 0)
            {
                if (offset == 0 && !bloque.AsSpan(0, leidos).StartsWith(FirmaPdf))
                    throw new ContenidoInvalidoException("El contenido no empieza con %PDF-");
                hash.AppendData(bloque, 0, leidos);
                var parte = leidos == bloque.Length ? bloque : bloque[..leidos];
                conexion.Validar(recurso.WriteFile(out _, archivo, offset, parte), $"WriteFile {temporal}");
                offset += leidos;
            }
            if (offset == 0)
                throw new ContenidoInvalidoException("El cuerpo está vacío");

            var sha256 = hash.GetHashAndReset();
            if (sha256Esperado is not null && !CryptographicOperations.FixedTimeEquals(sha256, sha256Esperado))
                throw new DigestNoCoincideException();

            var existia = Existe(conexion, nombre);
            conexion.Validar(recurso.SetFileInformation(archivo,
                new FileRenameInformationType2 { FileName = nombre, ReplaceIfExists = true }), $"Renombrar a {nombre}");
            renombrado = true;
            return new ResultadoGuardar(!existia, offset, sha256);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bloque);
            Cerrar(conexion, archivo, borrar: !renombrado);
            pool.Devolver(conexion);
        }
    }

    /// <summary>
    /// Copia el PDF por bloques al destino. <paramref name="abrirDestino"/> recibe el tamaño para
    /// poder fijar Content-Length antes de enviar el primer byte. Devuelve false si no existe.
    /// </summary>
    public async Task<bool> LeerAsync(string nombre, Func<long, Stream> abrirDestino, CancellationToken ct)
    {
        ConexionSmb conexion;
        object archivo;
        try
        {
            (conexion, archivo) = await AbrirArchivoAsync(nombre,
                AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, ShareAccess.Read, CreateDisposition.FILE_OPEN, ct);
        }
        catch (SmbException ex) when (ex.Estado == NTStatus.STATUS_OBJECT_NAME_NOT_FOUND)
        {
            return false;
        }

        var recurso = conexion.Recurso!;
        try
        {
            conexion.Validar(recurso.GetFileInformation(out var informacion, archivo,
                FileInformationClass.FileStandardInformation), $"GetFileInformation {nombre}");
            var tamano = ((FileStandardInformation)informacion).EndOfFile;
            var destino = abrirDestino(tamano);
            var tamanoBloque = (int)Math.Min(BloqueSmb, conexion.Cliente.MaxReadSize);
            for (long offset = 0; offset < tamano;)
            {
                var estado = recurso.ReadFile(out var datos, archivo, offset, tamanoBloque);
                if (estado == NTStatus.STATUS_END_OF_FILE || datos is null || datos.Length == 0) break;
                conexion.Validar(estado, $"ReadFile {nombre}");
                await destino.WriteAsync(datos, ct);
                offset += datos.Length;
            }
            return true;
        }
        finally
        {
            Cerrar(conexion, archivo, borrar: false);
            pool.Devolver(conexion);
        }
    }

    /// <summary>
    /// Toma una conexión y abre el archivo. Si una conexión reutilizada resultó estar caída
    /// (por ejemplo, el servidor la cerró), la descarta y reintenta una vez con una conexión
    /// nueva. Es seguro porque todavía no se leyó ni escribió nada.
    /// </summary>
    private async Task<(ConexionSmb Conexion, object Archivo)> AbrirArchivoAsync(string ruta,
        AccessMask acceso, ShareAccess compartir, CreateDisposition disposicion, CancellationToken ct)
    {
        for (var intento = 1; ; intento++)
        {
            var conexion = await pool.TomarAsync(ct);
            try
            {
                var estado = conexion.Recurso!.CreateFile(out var archivo, out _, ruta, acceso, FileAttributes.Normal,
                    compartir, disposicion,
                    CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
                conexion.Validar(estado, $"CreateFile {ruta}");
                return (conexion, archivo);
            }
            catch (Exception ex)
            {
                if (ex is not SmbException) conexion.MarcarRota();
                var reintentar = conexion.Rota && conexion.Usos > 1 && intento == 1;
                pool.Devolver(conexion);
                if (!reintentar) throw;
                pool.ContarReintento();
            }
        }
    }

    private static bool Existe(ConexionSmb conexion, string nombre)
    {
        var estado = conexion.Recurso!.CreateFile(out var archivo, out _, nombre,
            (AccessMask)FileAccessMask.FILE_READ_ATTRIBUTES, FileAttributes.Normal,
            ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_NON_DIRECTORY_FILE, null);
        if (estado == NTStatus.STATUS_OBJECT_NAME_NOT_FOUND) return false;
        conexion.Validar(estado, $"CreateFile {nombre}");
        conexion.Recurso.CloseFile(archivo);
        return true;
    }

    /// <summary>
    /// Cierra el handle del archivo (y lo borra si se pide). Si el cierre falla, la conexión no
    /// vuelve al pool: podría quedar con un archivo abierto en el servidor.
    /// </summary>
    private static void Cerrar(ConexionSmb conexion, object archivo, bool borrar)
    {
        try
        {
            if (borrar)
                conexion.Recurso!.SetFileInformation(archivo, new FileDispositionInformation { DeletePending = true });
            if (conexion.Recurso!.CloseFile(archivo) != NTStatus.STATUS_SUCCESS)
                conexion.MarcarRota();
        }
        catch (Exception)
        {
            conexion.MarcarRota();
        }
    }
}

public sealed record OpcionesSmb(
    string Servidor, string Recurso, string Dominio, string Usuario, string Clave,
    int MaxConexiones, TimeSpan Inactividad, TimeSpan VidaMaxima,
    TimeSpan TimeoutConexion, TimeSpan TimeoutOperacion, OpcionesCircuito Circuito);

/// <summary>Circuit breaker de las conexiones a Windows (variables SMB_CB_*).</summary>
public sealed record OpcionesCircuito(double ProporcionFallas, int MinimoIntentos, TimeSpan Ventana, TimeSpan DuracionAbierto);

public sealed record ResultadoGuardar(bool Creado, long Bytes, byte[] Sha256);

public sealed class SmbException(string operacion, NTStatus estado) : IOException($"{operacion}: {estado}")
{
    public NTStatus Estado { get; } = estado;
}

public sealed class ContenidoInvalidoException(string mensaje) : Exception(mensaje);

public sealed class DigestNoCoincideException()
    : Exception("El SHA-256 del cuerpo no coincide con la cabecera Content-Digest");
