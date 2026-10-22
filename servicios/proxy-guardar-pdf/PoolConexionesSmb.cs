using System.Net;
using System.Net.Sockets;
using Polly;
using Polly.CircuitBreaker;
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
/// <item>Abrir una conexión tiene un tiempo máximo (<c>TimeoutConexion</c>) y pasa por un
/// <i>circuit breaker</i>: si Windows falla de forma sostenida, las peticiones que necesitan una
/// conexión nueva se rechazan al instante en lugar de esperar al servidor caído.</item>
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
    private readonly ResiliencePipeline _circuito;
    private readonly CircuitBreakerStateProvider _estadoCircuito = new();
    private bool _cerrado;
    private long _fallasAlAbrir, _timeoutsAlAbrir, _rechazosCircuito, _aperturasCircuito;

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
        var cb = opciones.Circuito;
        _circuito = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = cb.ProporcionFallas,
                MinimumThroughput = cb.MinimoIntentos,
                SamplingDuration = cb.Ventana,
                BreakDuration = cb.DuracionAbierto,
                StateProvider = _estadoCircuito,
                // Cuentan las fallas de red, SMB y timeout al abrir. No cuenta que el cliente
                // cancele su petición (OperationCanceledException).
                ShouldHandle = args => ValueTask.FromResult(args.Outcome.Exception is IOException),
                OnOpened = args =>
                {
                    Interlocked.Increment(ref _aperturasCircuito);
                    _log.LogError(args.Outcome.Exception,
                        "Circuito SMB abierto por {Segundos} s: fallan las conexiones a {Servidor}", args.BreakDuration.TotalSeconds, opciones.Servidor);
                    return default;
                },
                OnHalfOpened = _ =>
                {
                    _log.LogWarning("Circuito SMB semiabierto: se prueba una conexión a {Servidor}", opciones.Servidor);
                    return default;
                },
                OnClosed = _ =>
                {
                    _log.LogInformation("Circuito SMB cerrado: {Servidor} responde de nuevo", opciones.Servidor);
                    return default;
                },
            })
            .Build();
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

            var nueva = await AbrirAsync(ct);
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

    /// <summary>Abre una conexión nueva a través del circuit breaker, con tiempo máximo.</summary>
    private async Task<ConexionSmb> AbrirAsync(CancellationToken ct)
    {
        try
        {
            return await _circuito.ExecuteAsync(static (estado, t) => new ValueTask<ConexionSmb>(ConexionSmb.AbrirAsync(estado, t)), _opciones, ct);
        }
        catch (BrokenCircuitException)
        {
            Interlocked.Increment(ref _rechazosCircuito);
            throw;
        }
        catch (TimeoutConexionSmbException)
        {
            Interlocked.Increment(ref _fallasAlAbrir);
            Interlocked.Increment(ref _timeoutsAlAbrir);
            throw;
        }
        catch (IOException)
        {
            Interlocked.Increment(ref _fallasAlAbrir);
            throw;
        }
    }

    /// <summary>Estado del circuit breaker: Closed, Open, HalfOpen o Isolated.</summary>
    public CircuitState EstadoCircuito => _estadoCircuito.CircuitState;

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
            circuito = new
            {
                estado = _estadoCircuito.CircuitState.ToString(),
                aperturas = Interlocked.Read(ref _aperturasCircuito),
                rechazos = Interlocked.Read(ref _rechazosCircuito),
                fallasAlAbrir = Interlocked.Read(ref _fallasAlAbrir),
                timeoutsAlAbrir = Interlocked.Read(ref _timeoutsAlAbrir),
            },
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

    /// <summary>
    /// Abre la conexión en como máximo <c>TimeoutConexion</c>: resolución DNS, conexión TCP al 445,
    /// negociación, login y TreeConnect. Cada respuesta SMB (también las de las operaciones
    /// posteriores) espera como máximo <c>TimeoutOperacion</c>.
    /// </summary>
    public static async Task<ConexionSmb> AbrirAsync(OpcionesSmb opciones, CancellationToken ct)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(opciones.TimeoutConexion);
        SMB2Client? cliente = null;
        Task<ConexionSmb>? apertura = null;
        try
        {
            var direcciones = await Dns.GetHostAddressesAsync(opciones.Servidor, AddressFamily.InterNetwork, limite.Token);
            if (direcciones.Length == 0) throw new IOException($"{opciones.Servidor} no tiene dirección IPv4");
            var ip = direcciones[0];
            // SMBLibrary conecta el socket de forma bloqueante y sin tiempo máximo: con el servidor
            // caído, el hilo quedaría esperando lo que el sistema operativo tarde en desistir
            // (unos 2 minutos en Linux). Por eso primero se prueba el 445 con un connect asíncrono
            // cancelable, que no ocupa hilos.
            using (var sonda = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
                await sonda.ConnectAsync(new IPEndPoint(ip, 445), limite.Token);

            cliente = new SMB2Client(true);
            var c = cliente;
            var timeoutMs = (int)opciones.TimeoutOperacion.TotalMilliseconds;
            apertura = Task.Run(() => Conectar(c, ip, timeoutMs, opciones));
            return await apertura.WaitAsync(limite.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Descartar(cliente, apertura);
            throw new TimeoutConexionSmbException(opciones.Servidor, opciones.TimeoutConexion);
        }
        catch (SocketException ex)
        {
            Descartar(cliente, apertura);
            throw new IOException($"No se pudo conectar a {opciones.Servidor}:445: {ex.SocketErrorCode}", ex);
        }
        catch (OperationCanceledException)
        {
            Descartar(cliente, apertura);
            throw;
        }
    }

    // Si se cortó por tiempo, la apertura puede seguir en curso: se corta el socket y, si
    // llegara a terminar bien, la conexión se cierra para no dejar sesiones huérfanas.
    private static void Descartar(SMB2Client? cliente, Task<ConexionSmb>? apertura)
    {
        if (apertura is null) { cliente?.Disconnect(); return; }
        cliente?.Disconnect();
        _ = apertura.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) t.Result.Dispose();
            else _ = t.Exception;
        }, TaskScheduler.Default);
    }

    // SMBLibrary 1.5.8.1 espera cada respuesta SMB 5 s fijos: la sobrecarga de Connect que recibe
    // el tiempo no es pública. Se ajusta el campo privado que leen todas las esperas (login,
    // TreeConnect, lecturas y escrituras). Si una versión futura lo cambia, el proxy no arranca
    // (ver VerificarCompatibilidad) en lugar de quedar sin timeout sin avisar.
    private static readonly System.Reflection.FieldInfo? CampoTimeoutRespuesta = typeof(SMB2Client)
        .GetField("m_responseTimeoutInMilliseconds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    /// <summary>Null si esta versión de SMBLibrary permite fijar el timeout de respuesta; si no, el motivo.</summary>
    public static string? VerificarCompatibilidad() => CampoTimeoutRespuesta?.FieldType == typeof(int) ? null
        : "SMBLibrary no tiene el campo m_responseTimeoutInMilliseconds: no se puede aplicar SMB_TIMEOUT_OPERACION_SEGUNDOS. Revise la versión del paquete (probado con 1.5.8.1)";

    private static ConexionSmb Conectar(SMB2Client cliente, IPAddress ip, int timeoutMs, OpcionesSmb opciones)
    {
        if (!cliente.Connect(ip, SMBTransportType.DirectTCPTransport))
            throw new IOException($"No se pudo conectar a {opciones.Servidor}:445");
        CampoTimeoutRespuesta!.SetValue(cliente, timeoutMs);
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

    // STATUS_IO_TIMEOUT: Windows no respondió dentro de SMB_TIMEOUT_OPERACION_SEGUNDOS. La sesión
    // ya no es confiable (la respuesta podría llegar tarde), así que también se descarta.
    public static bool EsErrorDeConexion(NTStatus estado) => estado is
        NTStatus.STATUS_IO_TIMEOUT or
        StatusSesionExpirada or
        NTStatus.STATUS_USER_SESSION_DELETED or
        NTStatus.STATUS_NETWORK_NAME_DELETED or
        NTStatus.STATUS_INVALID_SMB or
        NTStatus.STATUS_INVALID_HANDLE;

    public void Dispose()
    {
        // Cierre ordenado si todavía hay conexión: TreeDisconnect, Logoff y cierre TCP. Si la
        // conexión está rota (por ejemplo, un STATUS_IO_TIMEOUT) no se espera a Windows: cada paso
        // del cierre ordenado esperaría SMB_TIMEOUT_OPERACION_SEGUNDOS una respuesta que no llega.
        try
        {
            if (!Rota && Cliente.IsConnected)
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

/// <summary>Abrir la conexión SMB tardó más que <c>SMB_TIMEOUT_CONEXION_SEGUNDOS</c>.</summary>
public sealed class TimeoutConexionSmbException(string servidor, TimeSpan limite)
    : IOException($"{servidor} no respondió en {limite.TotalSeconds:0.#} s al abrir la conexión SMB");
