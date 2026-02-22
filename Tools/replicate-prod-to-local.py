"""Replica la BD de PRODUCCION (Render) en un Postgres 17 local (Docker).

USO:  python Tools/replicate-prod-to-local.py
  - Lee la connection string de prod de appsettings.Development.json
    (clave PostgresConnection_RENDER_PROD_BACKUP o PostgresConnection si apunta a Render).
  - Crea/reutiliza el contenedor 'inspecciono-dev-db' (postgres:17, puerto 5434,
    volumen persistente inspecciono_dev_data).
  - pg_dump (SOLO LECTURA sobre prod) + pg_restore en local.
  - Sincroniza __EFMigrationsHistory con las migraciones del repo (evita el drift
    que hace que el backend grite "migraciones sin aplicar" en cada arranque).
  - Vacia la cola Hangfire de la COPIA local (que no re-ejecute jobs de prod).
  - Re-ejecutable: borra y recrea el schema local en cada pasada.

Requiere: Docker Desktop arrancado. Prod NO se modifica nunca.
Conexion local resultante:
  Host=localhost;Port=5434;Database=inspecciono_dev;Username=dev;Password=devlocal
"""
import os, re, subprocess, sys, time, urllib.parse

APP = r"C:\Users\Diego\OneDrive - Educacyl\Escritorio\proyecto\NewApi\appsettings.Development.json"
CONTAINER = "inspecciono-dev-db"
LOCAL_USER, LOCAL_PASS, LOCAL_DB, LOCAL_PORT = "dev", "devlocal", "inspecciono_dev", "5434"

def run(args, **kw):
    return subprocess.run(args, capture_output=True, text=True, **kw)

# 1) Leer connection string de prod del JSON local (no se imprime nunca)
raw = open(APP, encoding="utf-8-sig").read()
m = re.search(r'"PostgresConnection_RENDER_PROD_BACKUP"\s*:\s*"([^"]+)"', raw) or re.search(r'"PostgresConnection"\s*:\s*"([^"]+)"', raw)
if not m:
    print("ERROR: no encuentro PostgresConnection en el JSON"); sys.exit(1)
cs = m.group(1)
kv = dict(p.split("=", 1) for p in cs.split(";") if "=" in p)
host, port = kv["Host"], kv.get("Port", "5432")
user, pwd, db = kv["Username"], kv["Password"], kv["Database"]
prod_uri = f"postgresql://{urllib.parse.quote(user)}:{urllib.parse.quote(pwd)}@{host}:{port}/{db}?sslmode=require"
print(f"[1/7] Prod: host={host} db={db} (password oculta)")

# 2) Contenedor local persistente
existing = run(["docker", "ps", "-aq", "-f", f"name=^{CONTAINER}$"]).stdout.strip()
if existing:
    print(f"[2/7] Contenedor {CONTAINER} ya existe -> lo reutilizo (docker start)")
    run(["docker", "start", CONTAINER])
else:
    r = run(["docker", "run", "-d", "--name", CONTAINER,
             "-p", f"{LOCAL_PORT}:5432",
             "-e", f"POSTGRES_USER={LOCAL_USER}",
             "-e", f"POSTGRES_PASSWORD={LOCAL_PASS}",
             "-e", f"POSTGRES_DB={LOCAL_DB}",
             "-v", "inspecciono_dev_data:/var/lib/postgresql/data",
             "--restart", "unless-stopped",
             "postgres:17-alpine"])
    if r.returncode != 0:
        print("ERROR docker run:", r.stderr); sys.exit(1)
    print(f"[2/7] Contenedor {CONTAINER} creado (puerto {LOCAL_PORT}, volumen persistente)")

# 3) Esperar a que Postgres acepte conexiones
for i in range(60):
    if run(["docker", "exec", CONTAINER, "pg_isready", "-U", LOCAL_USER, "-d", LOCAL_DB]).returncode == 0:
        break
    time.sleep(2)
else:
    print("ERROR: Postgres local no responde"); sys.exit(1)
print("[3/7] Postgres local listo")

# 4) Dump de PROD (solo lectura) con pg_dump 17 del contenedor
print("[4/7] pg_dump de prod (puede tardar un poco)...")
r = run(["docker", "exec", CONTAINER, "pg_dump", f"--dbname={prod_uri}",
         "--no-owner", "--no-privileges", "-Fc", "-f", "/tmp/prod.dump"])
if r.returncode != 0:
    print("ERROR pg_dump:", r.stderr[-1500:]); sys.exit(1)
size = run(["docker", "exec", CONTAINER, "sh", "-c", "ls -lh /tmp/prod.dump | awk '{print $5}'"]).stdout.strip()
print(f"      dump OK ({size})")

# 5) Restaurar en local (BD limpia: drop schema public+hangfire por si es re-ejecucion)
run(["docker", "exec", CONTAINER, "psql", "-U", LOCAL_USER, "-d", LOCAL_DB, "-c",
     "DROP SCHEMA IF EXISTS public CASCADE; DROP SCHEMA IF EXISTS hangfire CASCADE; CREATE SCHEMA public;"])
print("[5/7] pg_restore en local...")
r = run(["docker", "exec", CONTAINER, "pg_restore", "--no-owner", "--no-privileges",
         "-U", LOCAL_USER, "-d", LOCAL_DB, "/tmp/prod.dump"])
# pg_restore devuelve !=0 con warnings benignos; comprobamos con counts despues
if r.returncode != 0:
    tail = (r.stderr or "")[-800:]
    print("      pg_restore termino con avisos (se validara con counts):")
    print("      " + tail.replace(chr(10), chr(10) + "      "))

run(["docker", "exec", CONTAINER, "rm", "-f", "/tmp/prod.dump"])

# 6) Sincronizar __EFMigrationsHistory con las migraciones del repo.
#    POR QUE: el dump trae el historial de PROD, que suele ir ATRASADO respecto al
#    esquema (prod aplica mucho DDL a mano / con migraciones idempotentes sin
#    registrar la fila). Resultado: el backend en dev grita en cada arranque
#    "QUEDAN N MIGRACIONES SIN APLICAR" aunque el esquema ya este. Como la replica
#    ESPEJA el esquema de prod (fuente de verdad), marcamos como aplicadas todas las
#    migraciones presentes en /Migrations. ON CONFLICT DO NOTHING -> no pisa nada.
#    OJO: si tienes una migracion NUEVA aun NO desplegada a prod, su columna/tabla
#    no estara en la replica; aplica su DDL a dev a mano tras refrescar (y su fila
#    ya quedara marcada aqui, asi que no choca).
MIG_DIR = os.path.join(os.path.dirname(APP), "Migrations")
mig_ids = sorted(
    f[:-3] for f in os.listdir(MIG_DIR)
    if f.endswith(".cs") and not f.endswith(".Designer.cs") and "ModelSnapshot" not in f
)
if mig_ids:
    values = ",".join(f"('{mid}','10.0.8')" for mid in mig_ids)
    sql = (
        'CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" ('
        '"MigrationId" character varying(150) NOT NULL, '
        '"ProductVersion" character varying(32) NOT NULL, '
        'CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")); '
        'INSERT INTO "__EFMigrationsHistory" ("MigrationId","ProductVersion") VALUES '
        + values + ' ON CONFLICT ("MigrationId") DO NOTHING;'
    )
    r = run(["docker", "exec", CONTAINER, "psql", "-U", LOCAL_USER, "-d", LOCAL_DB,
             "-v", "ON_ERROR_STOP=1", "-c", sql])
    if r.returncode == 0:
        print(f"[6/7] __EFMigrationsHistory sincronizado ({len(mig_ids)} migraciones marcadas como aplicadas)")
    else:
        print(f"[6/7] AVISO sincronizando historial: {(r.stderr or '')[:300]}")
else:
    print(f"[6/7] AVISO: no encontre migraciones en {MIG_DIR}")

# 7) Higiene: vaciar la cola Hangfire LOCAL (que la copia no re-ejecute jobs de prod)
hf = run(["docker", "exec", CONTAINER, "psql", "-U", LOCAL_USER, "-d", LOCAL_DB, "-c",
          'TRUNCATE hangfire.jobqueue, hangfire.state, hangfire.job, hangfire.server, '
          'hangfire."set", hangfire.counter, hangfire.hash, hangfire.list CASCADE;'])
print("[7/7] Cola Hangfire local vaciada" if hf.returncode == 0 else f"[7/7] Aviso truncate hangfire: {hf.stderr[:300]}")

# Verificacion: counts de tablas clave
q = ("SELECT 'Users', count(*) FROM \"Users\" UNION ALL "
     "SELECT 'SearchHires', count(*) FROM \"SearchHires\" UNION ALL "
     "SELECT 'FinancialTransactions', count(*) FROM \"FinancialTransactions\" UNION ALL "
     "SELECT 'Appointments', count(*) FROM \"Appointments\" UNION ALL "
     "SELECT 'Disputes', count(*) FROM \"Disputes\" UNION ALL "
     "SELECT 'SystemStatuses', count(*) FROM \"SystemStatuses\" UNION ALL "
     "SELECT 'ExpertProfiles', count(*) FROM \"ExpertProfiles\" UNION ALL "
     "SELECT 'Logs', count(*) FROM \"Logs\"")
r = run(["docker", "exec", CONTAINER, "psql", "-U", LOCAL_USER, "-d", LOCAL_DB, "-t", "-A", "-c", q])
print("COUNTS LOCAL:")
print(r.stdout)
print("LISTO. Local: Host=localhost;Port=5434;Database=inspecciono_dev;Username=dev;Password=devlocal")
