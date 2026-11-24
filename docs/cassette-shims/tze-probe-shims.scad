// TZe cassette probe shims
// ------------------------------------------------------------------------------------
// A set of 32 numbered plates for finding out what the printer's five cassette sense
// pins mean. Each plate leaves some of the five positions open (the pin drops through)
// and closes the others (the pin is pushed in). Load a cassette with a plate fitted, read
// the colour bytes in WebLabelMaker's printer panel, and note them against the plate number.
//
// Read docs/tze-cassette-identification.md first: the base cassette must have all five
// positions OPEN, or the plates cannot produce every combination.
//
// !! THE DIMENSIONS BELOW ARE ESTIMATES TAKEN FROM A PHOTOGRAPH. !!
// Measure your cassette with calipers and correct them before printing the full set.
// Print plate 0 (or set `only = 0`) first and check that all five pins pass through it.
//
// Pin numbering, looking at the underside of the cassette with the hole group at the left:
//
//     P1  .        left column, top          (. = hole the printer does not read)
//     P2  P5       left column / right column, lower hole
//     P3
//     P4           left column, bottom
//
// Plate number = P1*1 + P2*2 + P3*4 + P4*8 + P5*16, where 1 means CLOSED (pin pushed in).
// Plate 0 has every position open; plate 31 closes all five.

/* [What to render] */
// -1 renders the whole set laid out for printing; 0..31 renders that one plate.
only = -1;

/* [Hole group: measure these] */
// Distance between the left and right columns, centre to centre (mm).
col_pitch = 2.8;
// Distance between rows, centre to centre (mm).
row_pitch = 2.4;
// Row of the right column that the printer reads (1 = top row). The photo suggests 2.
p5_row = 2;
// Diameter of the hole left for an open position: pin diameter plus clearance (mm).
open_d = 2.0;

/* [Plate] */
// Thickness under the cassette. Keep it thin: it lifts the cassette by this much.
plate_t = 0.4;
// Margin of plate around the outermost positions (mm).
margin = 2.2;
// Handle with the plate number, sticking out past the cassette edge. Trim it if it fouls the printer.
handle = true;
handle_len = 9;
handle_t = 1.0;

/* [Layout of the set] */
columns = 8;
gap = 3;

$fn = 32;

// Position of each sense pin on the plate: [x, y], origin at P1, y pointing down the column.
function pin_pos(k) =
    k < 4 ? [0, k * row_pitch]
          : [col_pitch, (p5_row - 1) * row_pitch];

function bit(n, k) = floor(n / pow(2, k)) % 2;

plate_w = col_pitch + 2 * margin;
plate_h = 3 * row_pitch + 2 * margin;

module plate(n) {
    difference() {
        union() {
            // the shim itself
            translate([-margin, -margin, 0]) cube([plate_w, plate_h, plate_t]);
            // handle on the left, carrying the number
            if (handle)
                translate([-margin - handle_len, -margin, 0]) cube([handle_len, plate_h, handle_t]);
        }
        // open positions: bit clear = hole
        for (k = [0 : 4])
            if (bit(n, k) == 0)
                translate([pin_pos(k)[0], pin_pos(k)[1], -1]) cylinder(d = open_d, h = plate_t + 2);
        // the plate number, cut into the handle
        if (handle)
            translate([-margin - handle_len / 2, -margin + plate_h / 2, handle_t - 0.4])
                linear_extrude(1)
                    text(str(n), size = min(handle_len * 0.5, plate_h * 0.45), halign = "center", valign = "center",
                         font = "Liberation Sans:style=Bold");
    }
}

module plate_set() {
    step_x = plate_w + (handle ? handle_len : 0) + gap;
    step_y = plate_h + gap;
    for (n = [0 : 31])
        translate([(n % columns) * step_x, floor(n / columns) * step_y, 0]) plate(n);
}

if (only < 0) plate_set(); else plate(only);
