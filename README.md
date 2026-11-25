# LauncherGT

Launcher de NamGT para Windows x64. La carpeta de distribución incluye XCL y VIctory. Para jugar, extraé la carpeta completa y abrí **Lanzador GTA VI.exe**.

Desde la versión 2.2.0, el launcher consulta la última publicación estable de este repositorio al abrirse, cada 15 minutos y al pulsar **Buscar cambios**. **Actualizar y reiniciar** descarga el ejecutable, comprueba la firma del proyecto y reemplaza el launcher. Podés seguir usando la versión instalada cuando estés sin conexión. Quienes tengan la versión 2.1.0 deben actualizarla manualmente una vez.

## Compilar en Windows

Ejecutá `./Build.ps1` desde PowerShell en Windows con .NET Framework 4.8. El ejecutable se genera en `dist`. Podés colocarlo junto a la carpeta `Emuladores` de tu instalación.

## Publicar una actualización

1. Cambiá la versión de `AssemblyVersion` en `Lanzador.cs` y usá cero como cuarto componente.
2. En la cuenta de Windows del titular, ejecutá `./PrepararVersion.ps1 -Clave RUTA_AL_ARCHIVO_DPAPI`. La clave privada de publicación se guarda cifrada fuera del repositorio.
3. Probá la compilación y subí los cambios de código. Creá un borrador de GitHub Release con la etiqueta correspondiente, por ejemplo `v2.2.1`.
4. Adjuntá los tres archivos de `release` con sus nombres exactos: `LauncherGT.exe`, `LauncherGT.exe.sig` y `version.txt`. Para la primera instalación podés adjuntar también el ZIP completo. Publicá como versión estable más reciente cuando estén listos los archivos.

Los usuarios reciben versiones publicadas. Podés seguir trabajando en la rama principal entre publicaciones.
