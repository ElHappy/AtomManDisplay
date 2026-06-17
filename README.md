# AtomManDisplay — C# para Windows

Daemon C# equivalente al `screen.py` de Linux, usando **LibreHardwareMonitor**
para obtener temperaturas, frecuencias y RPM de fan sin herramientas externas.

---

## Requisitos

| Requisito | Versión |
|-----------|---------|
| .NET SDK  | 8.0 o superior |
| Windows   | 10 / 11 (x64) |
| Permisos  | **Administrador** (para temperaturas de CPU/GPU vía LHM) |

---

## Dependencias NuGet (se instalan automáticamente)

| Paquete | Para qué |
|---------|----------|
| `LibreHardwareMonitorLib` | CPU/GPU temp, frecuencia, fan RPM |
| `System.IO.Ports`         | Comunicación serial con el display |
| `System.Management`       | WMI — vendor de RAM y modelo de disco |
| `NAudio`                  | Volumen del sistema (Core Audio API) |

---

## Compilar y ejecutar

```bash
# Instalar .NET 8 SDK si aún no lo tienes:
# https://dotnet.microsoft.com/download

# Compilar
cd AtomManDisplay
dotnet build -c Release

# Ejecutar (COM3 por defecto)
dotnet run -c Release

# Especificar puerto
dotnet run -c Release -- COM4

# Con dashboard en consola
dotnet run -c Release -- COM3 --dashboard

# Publicar como ejecutable autónomo (.exe)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
# El .exe queda en: bin\Release\net8.0-windows\win-x64\publish\AtomManDisplay.exe
```

> **Nota:** Ejecutar como Administrador para que LibreHardwareMonitor pueda
> leer temperaturas de CPU/GPU. Sin admin, las temperaturas mostrarán 0.

---

## Variables de entorno (configuración)

| Variable | Default | Descripción |
|----------|---------|-------------|
| `ATOMMAN_PORT`  | `COM3`          | Puerto COM del display |
| `OW_API_KEY`    | *(vacío)*       | API key de OpenWeather (gratis en openweathermap.org) |
| `OW_LOCATION`   | `Monterrey,MX`  | Ciudad o `lat,lon` |
| `OW_UNITS`      | `metric`        | `metric` (°C) o `imperial` (°F) |

Ejemplo con PowerShell:
```powershell
$env:OW_API_KEY  = "tu_api_key_aqui"
$env:OW_LOCATION = "Monterrey,MX"
$env:ATOMMAN_PORT = "COM3"
dotnet run -c Release -- --dashboard
```

---

## Protocolo (referencia)

```
ENQ  (display → PC):  AA 05 <SEQ> CC 33 C3 3C
REPLY (PC → display): AA <TileID> 00 <SEQ> {payload ASCII} CC 33 C3 3C
```

| SEQ  | TileID | Tile | Payload ejemplo |
|------|--------|------|-----------------|
| `'2'` | `0x53` | CPU  | `{CPU:Intel Core i9;Tempr:45;Useage:15;Freq:3600000;Tempr1:45;}` |
| `'3'` | `0x36` | GPU  | `{GPU:NVIDIA RTX 4060;Tempr:60;Useage:30}` |
| `'4'` | `0x49` | MEM  | `{Memory:Samsung;Used:8.0;Available:8.0;Total:16.0;Useage:50}` |
| `'5'` | `0x4F` | DISK | `{DiskName:Samsung SSD;Tempr:0;UsageSpace:500;AllSpace:1000;Usage:50}` |
| `'6'` | `0x6B` | DATE | `{Date:2026/06/15;Time:14:30:00;Week:1;Weather:1;TemprLo:20,TemprHi:35,Zone:,Desc:}` |
| `'7'` | `0x27` | NET  | `{SPEED:1200;NETWORK:1.2 M/s,300.0 K/s}` |
| `'9'` | `0x10` | VOL  | `{VOLUME:50}` |
| `'<'` | `0x1A` | BAT  | `{Battery:100}` (177 = sin batería / desktop) |

---

## Correr como servicio de Windows (opcional)

Usar NSSM (Non-Sucking Service Manager):

```powershell
# Descargar NSSM: https://nssm.cc/download
nssm install AtomMan "C:\ruta\a\AtomManDisplay.exe"
nssm set AtomMan AppDirectory "C:\ruta\a\"
nssm set AtomMan ObjectName "LocalSystem"   # para permisos de admin
nssm start AtomMan
```

---

## Troubleshooting

| Síntoma | Solución |
|---------|----------|
| Temperaturas en 0 | Ejecutar como Administrador |
| Puerto no encontrado | Verificar en Administrador de dispositivos cuál COM es el display |
| Display no muestra nada | Revisar que el unlock phase muestra "Activated" |
| Fan RPM siempre 0 | LHM puede no detectar el controlador de fan; es cosmético |
| Volumen -1 | NAudio necesita que el servicio de audio de Windows esté corriendo |
