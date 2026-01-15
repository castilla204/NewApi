# Solución: pg_dump usa versión incorrecta

## Problema

Tienes PostgreSQL 18 instalado, pero `pg_dump` sigue usando la versión 16.2:

```
pg_dump: detail: server version: 17.6; pg_dump version: 16.2
```

Esto significa que tienes múltiples versiones de PostgreSQL y el PATH está apuntando a la versión antigua.

## Soluciones

### ✅ Solución 1: Usar la ruta completa a pg_dump 18 (RÁPIDO)

**En Git Bash/MINGW64:**

```bash
PGPASSWORD='hrpQTD57m7H.C+&' "/c/Program Files/PostgreSQL/18/bin/pg_dump" -Fc -v --schema=public \
  -h aws-1-eu-west-2.pooler.supabase.com \
  -p 5432 \
  -U postgres.rveqsehzlvbttlpmsbmi \
  -d postgres \
  -f supabase_dump.dump
```

**O usando la variable de entorno:**

```bash
export PATH="/c/Program Files/PostgreSQL/18/bin:$PATH"
PGPASSWORD='hrpQTD57m7H.C+&' pg_dump -Fc -v --schema=public \
  -h aws-1-eu-west-2.pooler.supabase.com \
  -p 5432 \
  -U postgres.rveqsehzlvbttlpmsbmi \
  -d postgres \
  -f supabase_dump.dump
```

### ✅ Solución 2: Verificar qué versión está usando

```bash
which pg_dump
# Mostrará la ruta actual

pg_dump --version
# Mostrará la versión que está usando

"/c/Program Files/PostgreSQL/18/bin/pg_dump" --version
# Debe mostrar: pg_dump (PostgreSQL) 18.x
```

### ✅ Solución 3: Actualizar PATH permanentemente

**En Git Bash, edita `~/.bashrc` o `~/.bash_profile`:**

```bash
# Agregar al final del archivo
export PATH="/c/Program Files/PostgreSQL/18/bin:$PATH"
```

**Luego recarga:**

```bash
source ~/.bashrc
# o
source ~/.bash_profile
```

**Verificar:**

```bash
pg_dump --version
# Debe mostrar: pg_dump (PostgreSQL) 18.x
```

### ✅ Solución 4: Crear un alias (Alternativa)

**En `~/.bashrc` o `~/.bash_profile`:**

```bash
alias pg_dump='/c/Program\ Files/PostgreSQL/18/bin/pg_dump'
alias pg_restore='/c/Program\ Files/PostgreSQL/18/bin/pg_restore'
alias psql='/c/Program\ Files/PostgreSQL/18/bin/psql'
```

**Luego recarga:**

```bash
source ~/.bashrc
```

---

## Comando Completo Listo para Usar

**Opción A: Ruta completa (sin cambiar PATH):**

```bash
PGPASSWORD='hrpQTD57m7H.C+&' "/c/Program Files/PostgreSQL/18/bin/pg_dump" -Fc -v --schema=public \
  -h aws-1-eu-west-2.pooler.supabase.com \
  -p 5432 \
  -U postgres.rveqsehzlvbttlpmsbmi \
  -d postgres \
  -f supabase_dump.dump
```

**Opción B: Actualizar PATH en la sesión actual:**

```bash
export PATH="/c/Program Files/PostgreSQL/18/bin:$PATH"
PGPASSWORD='hrpQTD57m7H.C+&' pg_dump -Fc -v --schema=public \
  -h aws-1-eu-west-2.pooler.supabase.com \
  -p 5432 \
  -U postgres.rveqsehzlvbttlpmsbmi \
  -d postgres \
  -f supabase_dump.dump
```

---

## Verificar que Funciona

Después de ejecutar el comando, verifica:

```bash
# Si usaste ruta completa
"/c/Program Files/PostgreSQL/18/bin/pg_dump" --version

# Si actualizaste PATH
pg_dump --version
```

Ambos deben mostrar: `pg_dump (PostgreSQL) 18.x`

---

## Troubleshooting

### Error: "No such file or directory"
**Causa**: La ruta puede ser diferente

**Solución**: Busca dónde está instalado PostgreSQL 18:

```bash
find /c/Program\ Files -name "pg_dump.exe" 2>/dev/null
```

### Error: "Permission denied"
**Causa**: Problemas de permisos

**Solución**: Usa la ruta completa con comillas

### Error: Sigue usando versión 16
**Causa**: PATH no se actualizó correctamente

**Solución**: 
1. Cierra y abre una nueva terminal
2. O usa la ruta completa directamente

---

## Recomendación

**Para esta sesión, usa la ruta completa:**

```bash
PGPASSWORD='hrpQTD57m7H.C+&' "/c/Program Files/PostgreSQL/18/bin/pg_dump" -Fc -v --schema=public \
  -h aws-1-eu-west-2.pooler.supabase.com \
  -p 5432 \
  -U postgres.rveqsehzlvbttlpmsbmi \
  -d postgres \
  -f supabase_dump.dump
```

**Para futuras sesiones, actualiza el PATH permanentemente** (Solución 3).
