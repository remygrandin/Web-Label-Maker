// TZe dummy cassette for probing the printer's sense pins
// ------------------------------------------------------------------------------------
// A stand-in for a tape cassette: a base plate that seats in the printer's cassette bay,
// with pockets that take numbered shim cards over the bottom sense pins, and an upright
// holder that takes a shim card in front of the side sense pins. Posts bring it up to
// cassette height so the closed lid holds it down against the pins' springs.
//
// It carries no tape. It is for reading what the printer reports, not for printing.
//
// !! EVERY DIMENSION IN THE "MEASURE" SECTIONS IS A PLACEHOLDER. !!
// The defaults are rough guesses so that the file renders; a part printed from them will
// not fit. Measure a real cassette and the bay, enter the numbers, then print `part = "base"`
// in draft quality and correct it against the printer before printing any cards.
// See docs/tze-cassette-identification.md for the list of what to measure.
//
// Coordinates: looking at the UNDERSIDE of a cassette, x to the right, y up, origin at the
// lower-left corner of the cassette's outline. z = 0 is the underside (the bay floor).
//
// Card numbering is the same as tze-probe-shims.scad:
//   bottom card n = P1*1 + P2*2 + P3*4 + P4*8 + P5*16      (1 = closed, pin pushed in)
//   side card   n = A*1 + B*2 + C*4 + D*8

/* [What to render] */
// "base", "bottom_card", "side_card", "bottom_set" (all 32), "side_set" (all 16) or "assembly"
part = "assembly";
// Which pocket a bottom card is for: index into `pockets` below.
pocket_index = 0;
// Card number for the single-card parts.
number = 0;

/* [MEASURE: cassette outline] */
// Overall size of the cassette seen from below (mm).
body_w = 88;
body_d = 67;
corner_r = 3;
// Height of the cassette for the tape width you are imitating (mm): the posts reach this.
cassette_h = 16;

/* [MEASURE: things the base must clear] */
// Spindles, rollers and posts rising from the bay floor: [x, y, hole diameter].
clearances = [
    [22, 40, 16],
    [60, 22, 14],
    [66, 48, 12],
];
// Rectangular cut-outs, e.g. where the print head and platen enter: [x, y, width, depth].
cutouts = [
    [70, 26, 18, 30],
];

/* [MEASURE: bottom sense pins] */
// One pocket per group of bottom pins the printer has.
// Each: [x, y, pin positions], where (x, y) is the first pin and the positions are offsets
// from it, in the order P1..P5 (use fewer entries for a group with fewer pins).
pockets = [
    // group at the cassette's left edge (PT-E560BT): four in a column, one beside the second
    [4.0, 44.0, [[0, 0], [0, -2.4], [0, -4.8], [0, -7.2], [2.8, -2.4]]],
    // group in the middle of the bottom (PT-P300BT positions E, F, G, H, I)
    [40.0, 6.0, [[4, 4], [0, 0], [4, 0], [8, 0], [12, 0]]],
];
// Diameter of the opening left for an open pin: pin diameter plus clearance.
open_d = 2.0;

/* [MEASURE: side sense pins] */
// Where the side pins meet the cassette wall: [x, y] of the wall face, and the direction
// the pins travel in (degrees, 0 = towards +x).
side_at = [58, 60];
side_angle = 270;
// Heights of the side pins above the bay floor, A to D (mm).
side_pin_z = [4, 7, 10, 13];
// Sideways offset of each pin along the wall, A to D (mm). All zero = one vertical column.
side_pin_offset = [0, 0, 0, 0];

/* [Base and cards] */
base_t = 3.0;      // thickness of the base plate
card_t = 0.8;      // thickness of every shim card
card_margin = 3.0; // card material around the outermost pin
lip = 1.0;         // how far the base overlaps a bottom card to retain it
fit = 0.25;        // clearance between a card and its pocket
post_d = 7;        // diameter of the height posts
post_inset = 6;    // posts sit this far in from the corners

/* [Layout of a card set] */
gap = 3;
per_row = 8;

$fn = 40;

// ---- helpers -----------------------------------------------------------------------------

function bit(n, k) = floor(n / pow(2, k)) % 2;
function min_of(v, i = 0) = i == len(v) - 1 ? v[i] : min(v[i], min_of(v, i + 1));
function max_of(v, i = 0) = i == len(v) - 1 ? v[i] : max(v[i], max_of(v, i + 1));

function xs(pins) = [for (p = pins) p[0]];
function ys(pins) = [for (p = pins) p[1]];

// Lower-left corner and size of the card that covers a pin group, relative to its first pin.
function card_x0(pins) = min_of(xs(pins)) - card_margin;
function card_y0(pins) = min_of(ys(pins)) - card_margin;
function card_w(pins) = max_of(xs(pins)) - min_of(xs(pins)) + 2 * card_margin;
function card_d(pins) = max_of(ys(pins)) - min_of(ys(pins)) + 2 * card_margin;

module rounded_rect(w, d, r) {
    hull() for (x = [r, w - r], y = [r, d - r]) translate([x, y]) circle(r = r);
}

// ---- base ---------------------------------------------------------------------------------

module base() {
    difference() {
        union() {
            linear_extrude(base_t) rounded_rect(body_w, body_d, corner_r);
            // posts up to cassette height, so the lid presses the base down
            for (x = [post_inset, body_w - post_inset], y = [post_inset, body_d - post_inset])
                translate([x, y, 0]) cylinder(d = post_d, h = cassette_h);
            side_holder();
        }
        for (c = clearances) translate([c[0], c[1], -1]) cylinder(d = c[2], h = cassette_h + 2);
        for (c = cutouts) translate([c[0], c[1], -1]) cube([c[2], c[3], cassette_h + 2]);
        for (p = pockets) translate([p[0], p[1], 0]) pocket(p[2]);
    }
}

// A recess in the underside that a card drops into, flush with the underside, and a
// window above it for open pins to rise through. The bay floor keeps the card in.
module pocket(pins) {
    x0 = card_x0(pins); y0 = card_y0(pins); w = card_w(pins); d = card_d(pins);
    // recess for the card
    translate([x0 - fit, y0 - fit, -0.01]) cube([w + 2 * fit, d + 2 * fit, card_t + 0.1]);
    // window through the rest of the plate, smaller than the card so the card is retained
    translate([x0 + lip, y0 + lip, 0]) cube([w - 2 * lip, d - 2 * lip, base_t + 1]);
    // finger notch to push the card out from above
    translate([x0 + w / 2, y0 - fit, -0.01]) cylinder(d = 5, h = card_t + 0.1);
}

// ---- side holder ------------------------------------------------------------------------------

side_card_w = max_of(side_pin_offset) - min_of(side_pin_offset) + 2 * card_margin;
side_card_h = max_of(side_pin_z) + card_margin;
holder_wall = 1.6;

// An upright channel the side card slides into from above. The pins arrive along +x of the
// holder's own frame; behind the card is open space for the pins that pass through.
module side_holder() {
    translate([side_at[0], side_at[1], 0]) rotate([0, 0, side_angle])
        translate([0, -side_card_w / 2 - holder_wall - fit, 0])
            difference() {
                cube([card_t + 2 * fit + 2 * holder_wall + 4, side_card_w + 2 * holder_wall + 2 * fit, side_card_h]);
                // the slot for the card
                translate([holder_wall, holder_wall, base_t]) cube([card_t + 2 * fit, side_card_w + 2 * fit, side_card_h]);
                // opening in front of the card for the pins, and room behind it
                translate([-1, holder_wall + lip + fit, base_t]) cube([holder_wall + 2, side_card_w - 2 * lip, side_card_h]);
                translate([holder_wall + card_t + 2 * fit - 0.01, holder_wall + lip + fit, base_t])
                    cube([4 + holder_wall + 1, side_card_w - 2 * lip, side_card_h]);
            }
}

// ---- cards -------------------------------------------------------------------------------------

// Bottom card for a pocket: lies flat, holes at the open positions, number cut into the top face.
module bottom_card(n, pins) {
    x0 = card_x0(pins); y0 = card_y0(pins); w = card_w(pins); d = card_d(pins);
    difference() {
        translate([x0, y0, 0]) cube([w, d, card_t]);
        for (k = [0 : len(pins) - 1])
            if (bit(n, k) == 0) translate([pins[k][0], pins[k][1], -1]) cylinder(d = open_d, h = card_t + 2);
        // the number, in the margin beside the pins
        translate([x0 + w - 0.6, y0 + 0.6, card_t - 0.3]) linear_extrude(1)
            text(str(n), size = 2.2, halign = "right", valign = "bottom", font = "Liberation Sans:style=Bold");
    }
}

// Side card: stands upright in the holder. Drawn lying flat for printing: x across the wall, y = height.
module side_card(n) {
    lo = min_of(side_pin_offset);
    difference() {
        union() {
            cube([side_card_w, side_card_h - base_t, card_t]);
            // tab above the holder to pull it out by, carrying the number
            translate([0, side_card_h - base_t, 0]) cube([side_card_w, 6, card_t]);
        }
        for (k = [0 : len(side_pin_z) - 1])
            if (bit(n, k) == 0)
                translate([side_pin_offset[k] - lo + card_margin, side_pin_z[k] - base_t, -1]) cylinder(d = open_d, h = card_t + 2);
        translate([side_card_w / 2, side_card_h - base_t + 3, card_t - 0.3]) linear_extrude(1)
            text(str(n), size = 3, halign = "center", valign = "center", font = "Liberation Sans:style=Bold");
    }
}

module bottom_set(pins) {
    count = pow(2, len(pins));
    for (n = [0 : count - 1])
        translate([(n % per_row) * (card_w(pins) + gap) - card_x0(pins), floor(n / per_row) * (card_d(pins) + gap) - card_y0(pins), 0])
            bottom_card(n, pins);
}

module side_set() {
    count = pow(2, len(side_pin_z));
    for (n = [0 : count - 1])
        translate([(n % per_row) * (side_card_w + gap), floor(n / per_row) * (side_card_h - base_t + 6 + gap), 0]) side_card(n);
}

// ---- output -------------------------------------------------------------------------------------

if (part == "base") base();
else if (part == "bottom_card") bottom_card(number, pockets[pocket_index][2]);
else if (part == "side_card") side_card(number);
else if (part == "bottom_set") bottom_set(pockets[pocket_index][2]);
else if (part == "side_set") side_set();
else {
    // assembly preview: the base with card 0 in every pocket
    base();
    color("orange") for (p = pockets) translate([p[0], p[1], 0]) bottom_card(number, p[2]);
}
