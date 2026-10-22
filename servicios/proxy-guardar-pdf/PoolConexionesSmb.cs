using SMBLibrary;
using SMBLibrary.Client;

namespace ProxyGuardarPdf;

/// <summary>
/// Pool acotado de conexiones SMB autenticadas y conectadas al recurso.
/// <list type="bullet">
/// <item>Como máximo <c>MaxConexiones</c> conexiones por instancia: quien no consigue cupo espera
/// en <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/> sin bloquear hilos.</item>
/// <item>Cada conexión la usa una sola petición a la vez (SMB2Client no es seguro entre hilos).</item>
/// <item>Pila LIFO: se reutiliza la más reciente y las sobrantes envejecen y se cierran solas.</item>
/// <item>Se cierran las ociosas por más de <c>Inactividad</c> y las que superan <c>VidaMaxima</c>,
/// así que en reposo no queda ninguna conexión abierta contra el servidor.</item>
/// <item>Una conexión que falla a nivel de transporte o de sesión SMB se descarta, no vuelve al pool.</item>
/// </list>
/// </summary>
public sealed class PoolConexionesSmb : IDisposable
{
    private readonly OpcionesSmb _opciones;
    private readonly ILogger<PoolConexionesSmb> _log;
    private readonly SemaphoreSlim _cupos;
    private readonly Stack<ConexionSmb> _ociosas = new();
    private readonly Lock _candado = new();
    private readonly Timer _limpieza;
    private bool _cerrado;

    private int _prestadas, _maxPrestadas;
    private long _creadas, _reutilizadas, _cerradasInactivas, _cerradasVidaMaxima, _descartadas, _reintentos;
    private long _esperas, _esperaTotalMs, _esperaMaximaMs;

    // Histograma: cuántas conexiones estaban en uso (incluida la propia) cada vez que una petición
    // obtuvo cupo. Muestra la concurrencia real, no una foto cada tantos segundos.
    private readonly long[] _concurrencia;
    private readonly List<ConexionSmb> _abiertas = [];

    public PoolConexionesSmb(OpcionesSmb opciones, ILogger<PoolConexionesSmb> log)
    {
        _opciones = opciones;
        _log = log;
        _cupos = new SemaphoreSlim(opciones.MaxConexiones, opciones.MaxConexiones);
        _concurrencia = new long[opciones.MaxConexiones + 1];
        _limpieza = new Timer(_ => CerrarVencidas(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public async Task<ConexionSmb> TomarAsync(CancellationToken ct)
    {
        // Camino rápido sin esperar. Si no hay cupo, se mide cuánto tuvo que esperar la petición.
        if (!_cupos.Wait(0))
        {
            var inicio = Environment.TickCount64;
            await _cupos.WaitAsync(ct);
            var espera = Environment.TickCount64 - inicio;
            Interlocked.Increment(ref _esperas);
            Interlocked.Add(ref _esperaTotalMs, espera);
            for (var max = Interlocked.Read(ref _esperaMaximaMs); espera > max; max = Interlocked.Read(ref _esperaMaximaMs))
                Interlocked.CompareExchange(ref _esperaMaximaMs, espera, max);
        }
        // Se cuenta como prestada desde que tiene cupo, incluso mientras se abre una conexión nueva.
        var prestadas = Interlocked.Increment(ref _prestadas);
        for (var max = Volatile.Read(ref _maxPrestadas); prestadas > max; max = Volatile.Read(ref _maxPrestadas))
            Interlocked.CompareExchange(ref _maxPrestadas, prestadas, max);
        Interlocked.Increment(ref _concurrencia[Math.Min(prestadas, _concurrencia.Length - 1)]);
        try
        {
            while (true)
            {
                ConexionSmb? ociosa;
                lock (_candado)
                {
                    ObjectDisposedException.ThrowIf(_cerrado, this);
                    _ociosas.TryPop(out ociosa);
                }
                if (ociosa is null) break;
                if (ociosa.Vigente(_opciones, DateTime.UtcNow) is not { } motivo)
                {
                    Interlocked.Increment(ref _reutilizadas);
                    ociosa.Usos++;
                    return ociosa;
                }
                ContarCierre(motivo);
                Cerrar(ociosa, motivo);
            }

            var nueva = ConexionSmb.Abrir(_opciones);
            Interlocked.Increment(ref _creadas);
            lock (_candado) _abiertas.Add(nueva);
            nueva.Usos++;
            return nueva;
        }
        catch
        {
            Interlocked.Decrement(ref _prestadas);
            _cupos.Release();
            throw;
        }
    }

    /// <summary>Devuelve la conexión al pool, o la cierra si quedó rota o el pool se está cerrando.</summary>
    public void Devolver(ConexionSmb conexion)
    {
        try
        {
            Interlocked.Decrement(ref _prestadas);
            if (conexion.Rota || !conexion.Cliente.IsConnected)
            {
                Interlocked.Increment(ref _descartadas);
                Cerrar(conexion, "rota");
                return;
            }
            conexion.UltimoUso = DateTime.UtcNow;
            lock (_candado)
            {
                if (!_cerrado)
                {
                    _ociosas.Push(conexion);
                    return;
                }
            }
            Cerrar(conexion, "pool cerrado");
        }
        finally
        {
            _cupos.Release();
        }
    }

    public void ContarReintento() => Interlocked.Increment(ref _reintentos);

    public object Metricas()
    {
        int ociosas;
        object[] conexiones;
        var ahora = DateTime.UtcNow;
        lock (_candado)
        {
            ociosas = _ociosas.Count;
            conexiones = _abiertas.Select(c => (object)new
            {
                id = c.Id,
                usos = c.Usos,
                edadSegundos = (int)(ahora - c.Creada).TotalSeconds,
                libreHaceMs = (long)(ahora - c.UltimoUso).TotalMilliseconds,
                libre = _ociosas.Contains(c),
            }).ToArray();
        }
        return new
        {
            prestadas = Volatile.Read(ref _prestadas),
            maxPrestadas = Volatile.Read(ref _maxPrestadas),
            ociosas,
            creadas = Interlocked.Read(ref _creadas),
            reutilizadas = Interlocked.Read(ref _reutilizadas),
            cerradasInactivas = Interlocked.Read(ref _cerradasInactivas),
            cerradasVidaMaxima = Interlocked.Read(ref _cerradasVidaMaxima),
            descartadas = Interlocked.Read(ref _descartadas),
            reintentos = Interlocked.Read(ref _reintentos),
            esperasPorCupo = Interlocked.Read(ref _esperas),
            esperaTotalMs = Interlocked.Read(ref _esperaTotalMs),
            esperaMaximaMs = Interlocked.Read(ref _esperaMaximaMs),
            concurrenciaAlTomar = Enumerable.Range(1, _concurrencia.Length - 1)
                .ToDictionary(i => i.ToString(), i => Interlocked.Read(ref _concurrencia[i])),
            conexiones,
        };
    }

    private void ContarCierre(string motivo)
    {
        if (motivo == ConexionSmb.MotivoVidaMaxima) Interlocked.Increment(ref _cerradasVidaMaxima);
        else if (motivo == ConexionSmb.MotivoInactividad) Interlocked.Increment(ref _cerradasInactivas);
        else Interlocked.Increment(ref _descartadas);
    }

    private void CerrarVencidas()
    {
        var ahora = DateTime.UtcNow;
        var vencidas = new List<(ConexionSmb Conexion, string Motivo)>();
        lock (_candado)
        {
            if (_cerrado || _ociosas.Count == 0) return;
            // La pila queda en el mismo orden: se sacan todas y se devuelven las vigentes.
            var vigentes = new List<ConexionSmb>(_ociosas.Count);
            while (_ociosas.TryPop(out var conexion))
            {
                if (conexion.Vigente(_opciones, ahora) is { } motivo) vencidas.Add((conexion, motivo));
                else vigentes.Add(conexion);
            }
            for (var i = vigentes.Count - 1; i >= 0; i--) _ociosas.Push(vigentes[i]);
        }
        // El cierre hace viajes de red: fuera del candado.
        foreach (var (conexion, motivo) in vencidas)
        {
            ContarCierre(motivo);
            Cerrar(conexion, motivo);
        }
    }

    private void Cerrar(ConexionSmb conexion, string motivo)
    {
        lock (_candado) _abiertas.Remove(conexion);
        _log.LogDebug("Cerrando conexión SMB {Id} ({Motivo}, {Usos} usos)", conexion.Id, motivo, conexion.Usos);
        conexion.Dispose();
    }

    public void Dispose()
    {
        List<ConexionSmb> ociosas;
        lock (_candado)
        {
            if (_cerrado) return;
            _cerrado = true;
            ociosas = [.. _ociosas];
            _ociosas.Clear();
        }
        _limpieza.Dispose();
        foreach (var conexion in ociosas) Cerrar(conexion, "apagado");
        _log.LogInformation("Pool SMB cerrado: {Cerradas} conexiones ociosas cerradas", ociosas.Count);
    }
}

/// <summary>Conexión TCP, login NTLMv2 y TreeConnect al recurso, reutilizables entre operaciones.</summary>
public sealed class ConexionSmb : IDisposable
{
    public const string MotivoInactividad = "inactividad";
    public const string MotivoVidaMaxima = "vida máxima";

    private static int _siguienteId;

    public int Id { get; } = Interlocked.Increment(ref _siguienteId);
    public required SMB2Client Cliente { get; init; }
    public ISMBFileStore? Recurso { get; private set; }
    public DateTime Creada { get; } = DateTime.UtcNow;
    public DateTime UltimoUso { get; set; } = DateTime.UtcNow;
    public int Usos { get; set; }

    /// <summary>True si una operación detectó que la conexión o la sesión SMB ya no sirven.</summary>
    public bool Rota { get; private set; }

    public static ConexionSmb Abrir(OpcionesSmb opciones)
    {
        var cliente = new SMB2Client(true);
        if (!cliente.Connect(opciones.Servidor, SMBTransportType.DirectTCPTransport))
            throw new IOException($"No se pudo conectar a {opciones.Servidor}:445");
        var conexion = new ConexionSmb { Cliente = cliente };
        try
        {
            conexion.Validar(cliente.Login(opciones.Dominio, opciones.Usuario, opciones.Clave), "Login");
            conexion.Recurso = cliente.TreeConnect(opciones.Recurso, out var estado);
            conexion.Validar(estado, $"TreeConnect {opciones.Recurso}");
            return conexion;
        }
        catch
        {
            conexion.Dispose();
            throw;
        }
    }

    /// <summary>Null si se puede reutilizar; si no, el motivo para cerrarla.</summary>
    public string? Vigente(OpcionesSmb opciones, DateTime ahora) =>
        Rota || !Cliente.IsConnected ? "desconectada"
        : ahora - Creada > opciones.VidaMaxima ? MotivoVidaMaxima
        : ahora - UltimoUso > opciones.Inactividad ? MotivoInactividad
        : null;

    /// <summary>
    /// Lanza <see cref="SmbException"/> si el estado no es éxito, y marca la conexión como rota
    /// cuando el error es de transporte o de sesión (no de un archivo en particular).
    /// </summary>
    public void Validar(NTStatus estado, string operacion)
    {
        if (estado == NTStatus.STATUS_SUCCESS) return;
        if (EsErrorDeConexion(estado) || !Cliente.IsConnected) Rota = true;
        throw new SmbException(operacion, estado);
    }

    public void MarcarRota() => Rota = true;

    // SMBLibrary no define STATUS_NETWORK_SESSION_EXPIRED (MS-ERREF 0xC000035C).
    private const NTStatus StatusSesionExpirada = (NTStatus)0xC000035C;

    public static bool EsErrorDeConexion(NTStatus estado) => estado is
        StatusSesionExpirada or
        NTStatus.STATUS_USER_SESSION_DELETED or
        NTStatus.STATUS_NETWORK_NAME_DELETED or
        NTStatus.STATUS_INVALID_SMB or
        NTStatus.STATUS_INVALID_HANDLE;

    public void Dispose()
    {
        // Cierre ordenado si todavía hay conexión: TreeDisconnect, Logoff y cierre TCP.
        try
        {
            if (Cliente.IsConnected)
            {
                Recurso?.Disconnect();
                Cliente.Logoff();
            }
        }
        catch (Exception)
        {
            // La conexión ya estaba caída: solo queda liberar el socket.
        }
        finally
        {
            Cliente.Disconnect();
        }
    }
}
