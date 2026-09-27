"""
Image loading for banner previews, with no required dependencies.

PSD / PSB: Photoshop stores a flattened copy of the whole image (the
"composite") at the end of the file, unless "Maximize Compatibility" was
switched off when saving. We read that directly: RGB or greyscale, 8 or 16 bit,
raw or RLE (PackBits). Rows are compressed independently, so for a thumbnail
we decode only the rows we sample - a 1916x648 banner previews in milliseconds.

PNG / GIF: Tk reads them natively. Anything else (JPG, TGA...) needs Pillow,
used only if it happens to be installed.

Results are RGB bytes plus size; eg_gui turns them into a Tk PhotoImage (PPM).
"""
import os
import struct


class ImageError(Exception):
    pass


def _u16(b, o):
    return struct.unpack_from('>H', b, o)[0]


def _u32(b, o):
    return struct.unpack_from('>I', b, o)[0]


def _u64(b, o):
    return struct.unpack_from('>Q', b, o)[0]


def _packbits_row(data, start, length, out_len):
    """Decode one PackBits-compressed row."""
    out = bytearray()
    i, end = start, start + length
    while i < end and len(out) < out_len:
        n = data[i]
        i += 1
        if n < 128:                 # literal run of n+1 bytes
            out += data[i:i + n + 1]
            i += n + 1
        elif n > 128:               # repeat next byte 257-n times
            out += bytes([data[i]]) * (257 - n)
            i += 1
        # n == 128: no-op
    if len(out) < out_len:
        out += bytes(out_len - len(out))
    return bytes(out[:out_len])


def read_psd(path, max_width=480, background=(23, 27, 36)):
    """Return (width, height, rgb_bytes, info) for the PSD's composite, scaled
    down by whole-pixel sampling so width <= max_width."""
    with open(path, 'rb') as f:
        data = f.read()
    if data[:4] != b'8BPS':
        raise ImageError('not a Photoshop file')
    version = _u16(data, 4)
    if version not in (1, 2):
        raise ImageError(f'unknown PSD version {version}')
    big = version == 2              # PSB: some lengths are 64-bit
    channels = _u16(data, 12)
    height = _u32(data, 14)
    width = _u32(data, 18)
    depth = _u16(data, 22)
    mode = _u16(data, 24)
    if depth not in (8, 16):
        raise ImageError(f'{depth}-bit PSDs are not supported (8 or 16 only)')
    if mode not in (1, 3):          # 1 greyscale, 3 RGB
        names = {0: 'bitmap', 2: 'indexed', 4: 'CMYK', 7: 'multichannel', 8: 'duotone', 9: 'Lab'}
        raise ImageError(f'{names.get(mode, mode)} colour mode is not supported (save as RGB)')

    pos = 26
    pos += 4 + _u32(data, pos)                      # colour mode data
    pos += 4 + _u32(data, pos)                      # image resources
    pos += (8 + _u64(data, pos)) if big else (4 + _u32(data, pos))   # layer and mask info
    if pos + 2 > len(data):
        raise ImageError('no composite image (saved without "Maximize Compatibility"?)')
    compression = _u16(data, pos)
    pos += 2

    bpp = depth // 8
    row_bytes = width * bpp
    step = max(1, -(-width // max_width))           # ceil(width / max_width)
    out_w, out_h = -(-width // step), -(-height // step)
    colour_channels = 3 if mode == 3 else 1
    wanted = list(range(min(channels, colour_channels + 1)))   # colour + alpha if present

    # where each channel row starts
    if compression == 0:
        def row_data(ch, y):
            o = pos + (ch * height + y) * row_bytes
            return data[o:o + row_bytes]
    elif compression == 1:
        count_size = 4 if big else 2
        counts_start = pos
        n_rows = channels * height
        counts = [(_u32 if big else _u16)(data, counts_start + i * count_size) for i in range(n_rows)]
        offsets = [0] * n_rows
        o = counts_start + n_rows * count_size
        for i, c in enumerate(counts):
            offsets[i] = o
            o += c

        def row_data(ch, y):
            idx = ch * height + y
            return _packbits_row(data, offsets[idx], counts[idx], row_bytes)
    else:
        raise ImageError('ZIP-compressed composite is not supported (Photoshop normally uses RLE)')

    rgb = bytearray(out_w * out_h * 3)
    br, bg_, bb = background
    for oy, y in enumerate(range(0, height, step)):
        rows = [row_data(ch, y) for ch in wanted]
        base = oy * out_w * 3
        for ox, x in enumerate(range(0, width, step)):
            px = x * bpp                              # 16-bit: take the high byte
            if colour_channels == 3:
                r, g, b = rows[0][px], rows[1][px], rows[2][px]
            else:
                r = g = b = rows[0][px]
            if len(rows) > colour_channels:           # blend transparency over the panel colour
                a = rows[colour_channels][px]
                r = (r * a + br * (255 - a)) // 255
                g = (g * a + bg_ * (255 - a)) // 255
                b = (b * a + bb * (255 - a)) // 255
            o = base + ox * 3
            rgb[o] = r
            rgb[o + 1] = g
            rgb[o + 2] = b
    info = f'{width}x{height} PSD, {"RLE" if compression else "raw"}, {depth}-bit {"RGB" if mode == 3 else "grey"}'
    return out_w, out_h, bytes(rgb), info


def to_ppm(width, height, rgb):
    return b'P6 %d %d 255\n' % (width, height) + rgb


def load_preview(path, max_width=480):
    """Returns ('ppm', bytes, info) or ('file', path, info) for Tk-native files,
    or raises ImageError with a readable reason."""
    if not path or not os.path.exists(path):
        raise ImageError('file not found')
    ext = os.path.splitext(path)[1].lower()
    if ext in ('.psd', '.psb'):
        w, h, rgb, info = read_psd(path, max_width)
        return 'ppm', to_ppm(w, h, rgb), info
    if ext in ('.png', '.gif'):
        return 'file', path, ext[1:].upper()
    try:
        from PIL import Image          # optional
    except ImportError:
        raise ImageError(f'{ext} preview needs Pillow (pip install pillow); PSD and PNG work without it')
    im = Image.open(path).convert('RGB')
    if im.width > max_width:
        im = im.resize((max_width, max(1, im.height * max_width // im.width)))
    return 'ppm', to_ppm(im.width, im.height, im.tobytes()), f'{ext[1:].upper()} via Pillow'
