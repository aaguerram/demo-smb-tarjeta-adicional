#!/usr/bin/env python3
"""Muestrea el demo cada 5 s mientras corre la prueba de carga y escribe una línea JSON por muestra.

Por cada réplica de proxy-guardar-pdf: /metricas (hilos, ThreadPool, memoria, GC, pool SMB y
peticiones recibidas), memoria y CPU del contenedor y sus conexiones TCP al 445 por estado, vistas
con conntrack en el contenedor windows (desde un contenedor nicolaka/netshoot). Además, /metricas
del sd. Solo usa la librería estándar.

Uso: muestrear.py <archivo-salida> <segundos>
"""
import json
import subprocess
import sys
import time
import urllib.request

PROYECTO = "demo-smb-tarjeta-adicional"
RED = f"{PROYECTO}_default"
AYUDANTE = "smb-conntrack"


def sh(*args):
    return subprocess.run(args, capture_output=True, text=True).stdout


def obtener(url):
    try:
        with urllib.request.urlopen(url, timeout=3) as r:
            return json.load(r)
    except Exception:
        return None


def replicas():
    """{nombre: ip} de los contenedores del servicio proxy-guardar-pdf."""
    nombres = sh("docker", "ps", "--filter", f"label=com.docker.compose.project={PROYECTO}",
                 "--filter", "label=com.docker.compose.service=proxy-guardar-pdf",
                 "--format", "{{.Names}}").split()
    salida = {}
    for nombre in sorted(nombres):
        ip = sh("docker", "inspect", "-f", f'{{{{(index .NetworkSettings.Networks "{RED}").IPAddress}}}}', nombre).strip()
        salida[nombre] = ip
    return salida


def conexiones_445(ip_a_replica):
    """{replica: {estado: n}} según conntrack en el contenedor windows."""
    texto = sh("docker", "exec", AYUDANTE, "conntrack", "-L", "-p", "tcp", "--dport", "445")
    salida = {nombre: {} for nombre in ip_a_replica.values()}
    for linea in texto.splitlines():
        partes = linea.split()
        if len(partes) < 5 or not partes[4].startswith("src="):
            continue
        origen = ip_a_replica.get(partes[4][4:], "otro")
        estados = salida.setdefault(origen, {})
        estados[partes[3]] = estados.get(partes[3], 0) + 1
    return salida


def recursos(nombres):
    texto = sh("docker", "stats", "--no-stream", "--format", "{{.Name}}\t{{.MemUsage}}\t{{.CPUPerc}}", *nombres)
    salida = {}
    for linea in texto.splitlines():
        nombre, memoria, cpu = linea.split("\t")
        salida[nombre] = {"mem": memoria.split(" /")[0], "cpu": cpu}
    return salida


def main():
    archivo, duracion = sys.argv[1], int(sys.argv[2])
    sh("docker", "rm", "-f", AYUDANTE)
    sh("docker", "run", "-d", "--name", AYUDANTE, "--network", f"container:{PROYECTO}-windows-1",
       "--cap-add", "NET_ADMIN", "nicolaka/netshoot:v0.13", "sleep", "infinity")
    try:
        fin = time.time() + duracion
        with open(archivo, "w") as salida:
            while time.time() < fin:
                inicio = time.time()
                proxies = replicas()
                muestra = {
                    "t": int(inicio),
                    "proxies": {n: obtener(f"http://{ip}:8080/metricas") for n, ip in proxies.items()},
                    "sd": obtener("http://127.0.0.1:8081/metricas"),
                    "conntrack445": conexiones_445({ip: n for n, ip in proxies.items()}),
                    "docker": recursos(list(proxies) + [f"{PROYECTO}-sd-generar-solicitud-tarjeta-pdf-1",
                                                        f"{PROYECTO}-balanceador-proxy-1"]),
                }
                salida.write(json.dumps(muestra) + "\n")
                salida.flush()
                time.sleep(max(0, 5 - (time.time() - inicio)))
    finally:
        sh("docker", "rm", "-f", AYUDANTE)


if __name__ == "__main__":
    main()
