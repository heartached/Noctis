/**
 * Derives the accent ramp from the canonical brand hex in OKLCH, and prints
 * both the oklch() values and sRGB hex fallbacks. Also reports APCA-ish
 * contrast against the two page backgrounds so the text-safe steps are known
 * rather than guessed.
 */
const BRAND = '#E74856';

const srgbToLinear = (c) => (c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
const linearToSrgb = (c) => (c <= 0.0031308 ? c * 12.92 : 1.055 * c ** (1 / 2.4) - 0.055);

function hexToRgb(hex) {
  const n = parseInt(hex.slice(1), 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255].map((v) => v / 255);
}
const clamp01 = (v) => Math.min(1, Math.max(0, v));
function rgbToHex([r, g, b]) {
  return (
    '#' +
    [r, g, b]
      .map((v) => Math.round(clamp01(v) * 255).toString(16).padStart(2, '0'))
      .join('')
      .toUpperCase()
  );
}

function rgbToOklch(rgb) {
  const [r, g, b] = rgb.map(srgbToLinear);
  const l = Math.cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
  const m = Math.cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
  const s = Math.cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
  const L = 0.2104542553 * l + 0.793617785 * m - 0.0040720468 * s;
  const A = 1.9779984951 * l - 2.428592205 * m + 0.4505937099 * s;
  const B = 0.0259040371 * l + 0.7827717662 * m - 0.808675766 * s;
  const C = Math.hypot(A, B);
  let h = (Math.atan2(B, A) * 180) / Math.PI;
  if (h < 0) h += 360;
  return { L, C, h };
}

function oklchToRgb({ L, C, h }) {
  const hr = (h * Math.PI) / 180;
  const A = C * Math.cos(hr);
  const B = C * Math.sin(hr);
  const l = (L + 0.3963377774 * A + 0.2158037573 * B) ** 3;
  const m = (L - 0.1055613458 * A - 0.0638541728 * B) ** 3;
  const s = (L - 0.0894841775 * A - 1.291485548 * B) ** 3;
  return [
    4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
    -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
    -0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s,
  ].map(linearToSrgb);
}

const inGamut = (rgb) => rgb.every((v) => v >= -0.001 && v <= 1.001);

const base = rgbToOklch(hexToRgb(BRAND));
console.log(
  `canonical ${BRAND} = oklch(${base.L.toFixed(4)} ${base.C.toFixed(4)} ${base.h.toFixed(2)})`
);
console.log(`round-trip check -> ${rgbToHex(oklchToRgb(base))}\n`);

const h = base.h;
const STEPS = [
  ['50', 0.96, 0.03],
  ['100', 0.9, 0.06],
  ['300', 0.76, 0.14],
  ['500', base.L, base.C],
  ['600', 0.56, base.C],
  ['700', 0.46, base.C],
];

const WCAG = (fg, bg) => {
  const lum = (rgb) => {
    const [r, g, b] = rgb.map(srgbToLinear);
    return 0.2126 * r + 0.7152 * g + 0.0722 * b;
  };
  const a = lum(fg) + 0.05;
  const b2 = lum(bg) + 0.05;
  return (Math.max(a, b2) / Math.min(a, b2)).toFixed(2);
};

const DARK_BG = hexToRgb('#08080A');
const LIGHT_BG = hexToRgb('#FBFAF8');

console.log('step  oklch                        hex       vs #08080A  vs #FBFAF8  gamut');
for (const [name, L, C] of STEPS) {
  const rgb = oklchToRgb({ L, C, h });
  const hex = rgbToHex(rgb);
  const ok = inGamut(rgb);
  console.log(
    `${name.padEnd(5)} oklch(${L.toFixed(4)} ${C.toFixed(4)} ${h.toFixed(2)})`.padEnd(45) +
      `${hex}   ${WCAG(rgb, DARK_BG).padStart(6)}      ${WCAG(rgb, LIGHT_BG).padStart(6)}      ${ok ? 'ok' : 'CLIPPED'}`
  );
}
