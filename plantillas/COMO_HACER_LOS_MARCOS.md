# Cómo hacer los marcos en Canva

## Formato
- **Tamaño:** 2500 × 3500 px, vertical (proporción 5:7, la de la figurita de 5×7 cm).
  En Canva: *Crear diseño → Tamaño personalizado → 2500 × 3500 px*.
- Con ese tamaño alcanza para la figurita impresa en la DNP y para la ampliación 20×30
  (la figurita entera a 20×28 cm, con bandas del color del fondo arriba y abajo).

## Pasos
1. Subí `plantilla_guias_2500x3500.png` a Canva y ponelo **arriba de todo**, ocupando todo el diseño. Bloqueá esa capa (candado).
2. Diseñá el marco debajo de la plantilla:
   - **Naranja:** logos, textos y todo lo importante tiene que quedar **adentro** de esta línea.
     El fondo sí puede llegar hasta el borde.
   - **Azul:** ventana sugerida para la foto. Ese hueco tiene que quedar **vacío y transparente**. Puede tener otra forma (ovalada, con cintas encima, etc.).
   - **Verde:** donde va a caer la cara, más o menos. No tapes esa zona.
3. **Borrá la capa de la plantilla** antes de exportar.
4. Exportá: *Compartir → Descargar → PNG*, con **"Fondo transparente"** tildado (requiere Canva Pro) y al tamaño 1×.
5. Nombres de archivo: `Fondo_1.png`, `Fondo_2.png`, `Fondo_3.png`. Van en la carpeta `assets` de la app.

## Chequeo rápido
- El PNG tiene que medir **2500 × 3500**.
- Al abrirlo, la ventana de la foto tiene que verse como un **damero** (transparente), no blanca.
- Usá los logos originales en alta (PNG grande o SVG), no capturas de pantalla.

## Marcos actuales (desde Canva)
Los 3 marcos de la app salen del diseño de Canva **"back_600"** (la copia con el hueco ensanchado,
ID `DAHWr480sps`; el original `DAHBzq9T8D4` quedó sin tocar). El hueco de la foto es un rectángulo
verde `#7ED957`. Para regenerarlos después de cambiar algo en Canva:
1. Exportar las 3 páginas en PNG a 2500 × 3500 (sin fondo transparente).
2. `python3 procesar_marcos.py pagina1.png pagina2.png pagina3.png` → genera `Fondo_1/2/3.png`
   (vuelve transparente el verde con bordes suaves y rellena el corte conocido de la cinta del marco 2).
3. Copiarlos a `src/KCMundial.App/assets/` (o a `C:\KCMundial2\assets\`).
