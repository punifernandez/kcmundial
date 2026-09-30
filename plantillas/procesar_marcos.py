"""
Convierte los PNG exportados de Canva (hueco de la foto en verde #7ED957) en marcos para la app:
- rellena cortes verdes angostos dentro de adornos (p. ej. la cinta del marco 2), continuando los colores,
- vuelve transparente el verde con bordes suaves y sin halo,
- deja el resultado en 2500×3500.

Uso: python3 procesar_marcos.py canva_1.png canva_2.png canva_3.png  -> Fondo_1.png, Fondo_2.png, Fondo_3.png
"""
import sys
from PIL import Image, ImageChops

SIZE = (2500, 3500)
MAX_GAP = 60  # px: tramos verdes más angostos que esto, con adorno a ambos lados, se rellenan
BLEND = 30    # px de cada lado del corte que se rehacen para que la transición sea suave
MIN_ROWS = 10 # zonas más bajas que esto son bordes finos del dibujo, no cortes: se dejan como están

# Cortes a rellenar, por marco: (x0, y0, x1, y1) en píxeles de 2500×3500. Solo ahí se busca: en el resto
# del diseño los huecos verdes angostos son a propósito (la foto se ve entre adornos).
# Marco 2: la cinta de arriba tiene un corte que antes tapaba el filete blanco.
GAP_AREAS = {2: [(1950, 300, 2300, 1000)]}


def is_green(p, key):
    r, g, b = p[:3]
    kg = key[1] - max(key[0], key[2])
    return g - max(r, b) > 0.5 * kg


def find_gaps(im, key):
    """Tramos verdes angostos con adorno a ambos lados: {y: (start, end)} agrupados por zona."""
    px = im.load()
    w, h = im.size
    segments = []
    for y in range(h):
        x = 0
        while x < w:
            if not is_green(px[x, y], key):
                x += 1
                continue
            start = x
            while x < w and is_green(px[x, y], key):
                x += 1
            end = x  # [start, end) es verde
            if start >= 3 and end + 2 < w and end - start <= MAX_GAP \
                    and not is_green(px[start - 3, y], key) and not is_green(px[end + 2, y], key):
                segments.append((y, start, end))
    # agrupar tramos de filas vecinas que se superponen en x
    regions = []
    for y, s0, e0 in segments:
        for r in regions:
            ly, ls, le = r[-1]
            if y - ly <= 2 and s0 < le + 4 and e0 > ls - 4:
                r.append((y, s0, e0))
                break
        else:
            regions.append([(y, s0, e0)])
    return regions


def fill_narrow_gaps(im, key, areas):
    """
    Rellena cada zona siguiendo la inclinación del dibujo: busca el desplazamiento vertical que mejor
    alinea la columna de la izquierda del corte con la de la derecha, e interpola a lo largo de esa diagonal.
    """
    px = im.load()
    h = im.size[1]
    regions = find_gaps(im, key)
    def inside(r):
        y, s0, e0 = r[len(r) // 2]
        return any(x0 <= s0 and e0 <= x1 and y0 <= y <= y1 for x0, y0, x1, y1 in areas)

    regions = [r for r in regions if r[-1][0] - r[0][0] + 1 >= MIN_ROWS and inside(r)]
    for region in regions:
        xl = max(0, min(s for _, s, _ in region) - 3 - BLEND)
        xr = min(im.size[0] - 1, max(e for _, _, e in region) + 2 + BLEND)
        ys = [y for y, _, _ in region]
        y0, y1 = min(ys), max(ys)

        def col(x, y):
            return px[x, max(0, min(h - 1, y))]

        best_d, best_err = 0, None
        for d in range(-120, 121, 2):
            err = 0
            for y in range(y0, y1 + 1, 2):
                a, b = col(xl, y), col(xr, y + d)
                err += sum(abs(a[c] - b[c]) for c in range(3))
            if best_err is None or err < best_err:
                best_d, best_err = d, err

        span = xr - xl
        # guardar las columnas de referencia antes de pisar nada
        ref_l = {y: col(xl, y) for y in range(0, h)}
        ref_r = {y: col(xr, y) for y in range(0, h)}

        def col(x, y):  # noqa: F811 - desde acá se leen las columnas originales
            y = max(0, min(h - 1, y))
            return ref_l[y] if x == xl else ref_r[y]

        print(f"  zona x={xl}..{xr} y={y0}..{y1} desplazamiento={best_d}")
        for y, s0, e0 in region:
            for x in range(xl + 1, xr):
                t = (x - xl) / span
                left = col(xl, round(y - t * best_d))
                right = col(xr, round(y + (1 - t) * best_d))
                if is_green(left, key) or is_green(right, key):
                    continue
                px[x, y] = tuple(round(left[c] * (1 - t) + right[c] * t) for c in range(3))
    return len(regions)


def key_out(im, key):
    kg = key[1] - max(key[0], key[2])
    R, G, B = im.split()
    max_rb = ImageChops.lighter(R, B)
    green = ImageChops.subtract(G, max_rb)
    lo, span = 0.15 * kg, 0.7 * kg
    alpha = green.point(lambda v: int(255 * max(0, min(1, 1 - (v - lo) / span))))
    semi = alpha.point(lambda v: 255 if v < 255 else 0)
    G2 = Image.composite(ImageChops.darker(G, max_rb), G, semi)  # quitar el verde de los bordes
    return Image.merge("RGBA", (R, G2, B, alpha))


for n, path in enumerate(sys.argv[1:], start=1):
    im = Image.open(path).convert("RGB").resize(SIZE, Image.LANCZOS)
    key = im.getpixel((SIZE[0] // 2, SIZE[1] // 2))  # el centro siempre es el hueco
    rows = fill_narrow_gaps(im, key, GAP_AREAS.get(n, [])) if n in GAP_AREAS else 0
    out = key_out(im, key)
    out.save(f"Fondo_{n}.png", optimize=True)
    transparent = out.getchannel("A").histogram()[0] / (SIZE[0] * SIZE[1])
    print(f"Fondo_{n}.png: clave {key}, zonas de cortes rellenadas {rows}, transparente {transparent:.1%}")
