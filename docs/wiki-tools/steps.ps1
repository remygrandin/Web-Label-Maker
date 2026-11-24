# Screenshot script for the documentation. Dot-sourced by cdp.ps1.
$app = 'http://localhost:5210/'

function Click-At([double]$x, [double]$y) {
    Send-Cdp 'Input.dispatchMouseEvent' @{ type = 'mousePressed'; x = $x; y = $y; button = 'left'; clickCount = 1 } | Out-Null
    Send-Cdp 'Input.dispatchMouseEvent' @{ type = 'mouseReleased'; x = $x; y = $y; button = 'left'; clickCount = 1 } | Out-Null
    Start-Sleep -Milliseconds 300
}
function Press-CtrlS {
    Send-Cdp 'Input.dispatchKeyEvent' @{ type = 'keyDown'; modifiers = 2; key = 's'; code = 'KeyS'; windowsVirtualKeyCode = 83 } | Out-Null
    Send-Cdp 'Input.dispatchKeyEvent' @{ type = 'keyUp'; modifiers = 2; key = 's'; code = 'KeyS'; windowsVirtualKeyCode = 83 } | Out-Null
    Start-Sleep -Milliseconds 1500
}
function Open-Label([string]$what, [string]$name) {
    Send-Cdp 'Page.navigate' @{ url = $app + '?open=' + [Uri]::EscapeDataString($what) } | Out-Null
    Start-Sleep -Milliseconds 800
    Wait-For "window.LM && document.querySelector('.docname') && document.querySelector('.docname').value === '$name' && LM.state && LM.state.frame"
    Eval @"
window.T = {
  wait: (ms) => new Promise(r => setTimeout(r, ms)),
  byText: (sel, txt) => [...document.querySelectorAll(sel)].find(e => e.textContent.replace(/\s+/g, ' ').trim().includes(txt)),
  leaf: (root, txt) => [...document.querySelectorAll(root + ' *')].find(e => e.children.length === 0 && e.textContent.trim() === txt),
  type: (el, v) => { el.value = v; el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true })); },
  tag: (title, name) => { const t = T.byText('.sect-title', title); if (t) t.parentElement.dataset.shot = name; return !!t; },
};
return true;
"@ | Out-Null
    Start-Sleep -Milliseconds 1500
}
function Blank-Click { Click-At 640 700 }

# ---- 1. editor overview ---------------------------------------------------------------
Open-Label 'builtin:builtin-asset-tag' 'Asset tag'
Blank-Click
Shot 'editor-overview'

# ---- 2. tools --------------------------------------------------------------------------
Eval "document.querySelectorAll('nav.rail .tool')[2].click(); await T.wait(400); return true;" | Out-Null
Shot 'tools-shapes' @('nav.rail', '.flyout') 6
Eval "document.querySelector('.menu-scrim').click(); await T.wait(300); return true;" | Out-Null

# ---- 3. a text object and its inspector -------------------------------------------------
Eval "T.leaf('aside', 'Acme Workshop').click(); await T.wait(600); return true;" | Out-Null
Shot 'inspector-text' @('aside.insp') 0

# ---- 4. variables, counter and a macro in a text -----------------------------------------
Blank-Click
Eval @"
T.byText('.sect', 'Variables').querySelector('.sect-title button').click(); await T.wait(500);
const rows = T.byText('.sect', 'Variables').querySelectorAll('.row');
const ins = rows[0].querySelectorAll('input');
ins[0].value = 'site'; ins[0].dispatchEvent(new Event('change', { bubbles: true })); await T.wait(300);
const ins2 = T.byText('.sect', 'Variables').querySelectorAll('.row')[0].querySelectorAll('input');
ins2[1].value = 'LYON'; ins2[1].dispatchEvent(new Event('input', { bubbles: true })); await T.wait(400);
T.leaf('aside', 'ID 000123').click(); await T.wait(600);
T.type(document.querySelector('#text-content'), '{site}-{seq:0000}'); await T.wait(700);
return true;
"@ | Out-Null
Shot 'text-macro' @('aside.insp') 0
Blank-Click
Eval "T.tag('Variables', 'vars'); T.tag('Counter', 'counter'); document.querySelector('[data-shot=vars]').scrollIntoView({ block: 'start' }); await T.wait(400); return true;" | Out-Null
Shot 'variables' @('[data-shot=vars]', '[data-shot=counter]') 0
Eval "document.querySelector('aside.insp').scrollTop = 0; await T.wait(300); return true;" | Out-Null
Shot 'label-with-variables' @('main.stage') 0

# a numeric field driven by an expression
Eval "T.leaf('aside', 'PROPERTY OF').click(); await T.wait(600); return true;" | Out-Null
Shot 'inspector-geometry' @('aside.insp') 0

# ---- 5. library ----------------------------------------------------------------------------
Blank-Click
Press-CtrlS
Open-Label 'builtin:builtin-wifi' 'Wi-Fi access'
Blank-Click
Press-CtrlS
Open-Label 'builtin:builtin-cable-flag' 'Cable flag'
Blank-Click
Press-CtrlS
Eval "T.type(Object.assign(document.createElement('input')), ''); return true;" | Out-Null
Shot 'topbar' @('header.topbar') 0

Eval "T.byText('header button', 'Library').click(); await T.wait(1500); return true;" | Out-Null
Wait-For "document.querySelector('.dialog .card')"
Start-Sleep -Milliseconds 1200
Shot 'library-labels' @('.dialog') 10
Eval "T.byText('.tab', 'Templates').click(); await T.wait(2500); return true;" | Out-Null
Shot 'library-templates' @('.dialog') 10
Eval "T.byText('.tab', 'Autosaved').click(); await T.wait(800); return true;" | Out-Null
Shot 'library-autosaved' @('.dialog') 10
Eval "document.querySelector('.dhead .ibtn[title=Close]').click(); await T.wait(300); return true;" | Out-Null

# ---- 6. file menu -------------------------------------------------------------------------
Eval "T.byText('header button', 'File').click(); await T.wait(400); return true;" | Out-Null
Shot 'file-menu' @('header .menu') 10
Eval "document.querySelector('.menu-scrim').click(); await T.wait(300); return true;" | Out-Null

# ---- 7. icons --------------------------------------------------------------------------------
Eval "[...document.querySelectorAll('nav.rail button.tool')].find(x => (x.dataset.tip || '').startsWith('Icons')).click(); await T.wait(600); T.type(document.querySelector('#icon-query'), 'warning'); return true;" | Out-Null
Wait-For "document.querySelectorAll('.icon-grid button').length > 20" 40
Start-Sleep -Milliseconds 800
Shot 'icons' @('.dialog') 10
Eval "document.querySelector('.dhead .ibtn[title=Close]').click(); await T.wait(300); return true;" | Out-Null

# ---- 8. printer ------------------------------------------------------------------------------
Eval "document.querySelector('.pchip').click(); await T.wait(500); return true;" | Out-Null
Shot 'printer-panel' @('.pchip', '.pmenu') 10
Eval "document.querySelector('.menu-scrim').click(); await T.wait(300); return true;" | Out-Null
Eval "document.querySelector('header .btn.primary').click(); await T.wait(600); return true;" | Out-Null
Wait-For "document.querySelector('.dialog .preview img')"
Start-Sleep -Milliseconds 800
Shot 'print-dialog' @('.dialog') 10
Eval "document.querySelector('.dhead .ibtn[title=Close]').click(); await T.wait(300); return true;" | Out-Null

# ---- 9. cabling layouts --------------------------------------------------------------------------
Shot 'layout-flag' @('main.stage') 0
Open-Label 'builtin:builtin-plain' 'Plain text'
Blank-Click
Eval "T.byText('.seg button', 'Ports').click(); await T.wait(1500); return true;" | Out-Null
Shot 'layout-ports'
