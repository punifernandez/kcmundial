namespace KCMundial.ShareServer;

public static class HtmlTemplates
{
    /// <summary>
    /// Mobile-friendly figurita page. {0}=page title, {1}=imageUrl, {2}=imageUrl (abrir), {3}=helper text.
    /// </summary>
    public const string FiguritaPage = """
<!DOCTYPE html>
<html lang="es">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{0}</title>
<style>
* { box-sizing: border-box; }
body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; margin: 0; padding: 16px; background: #111; color: #eee; min-height: 100vh; display: flex; flex-direction: column; align-items: center; justify-content: center; }
h1 { font-size: 1.25rem; margin: 0 0 16px; font-weight: 600; }
img { max-width: 100%; height: auto; border-radius: 8px; box-shadow: 0 4px 20px rgba(0,0,0,0.4); }
.actions { margin-top: 20px; display: flex; flex-wrap: wrap; gap: 10px; justify-content: center; }
button, a.btn { display: inline-block; padding: 12px 20px; font-size: 1rem; border-radius: 8px; border: none; cursor: pointer; text-decoration: none; font-weight: 600; transition: opacity 0.2s; }
button:active, a.btn:active { opacity: 0.8; }
.download { background: #1a73e8; color: #fff; }
.share { background: #34a853; color: #fff; }
.help { margin-top: 24px; font-size: 0.875rem; color: #9aa0a6; text-align: center; max-width: 320px; line-height: 1.4; }
.msg { margin-top: 8px; font-size: 0.8rem; color: #9aa0a6; display: none; }
</style>
</head>
<body>
<h1>Tu figurita KCMundial</h1>
<img src="{1}" alt="Figurita" id="preview" loading="lazy">
<div class="actions">
<button type="button" class="download" id="downloadBtn">Descargar</button>
<button type="button" class="share" id="shareBtn">Compartir</button>
</div>
<p class="msg" id="msg"></p>
<p class="help">{3}</p>
<script>
(function() {
var downloadBtn = document.getElementById('downloadBtn');
var shareBtn = document.getElementById('shareBtn');
var msgEl = document.getElementById('msg');
var imageUrl = '{2}';
function showMsg(text) {
  msgEl.textContent = text;
  msgEl.style.display = 'block';
  setTimeout(function() { msgEl.style.display = 'none'; msgEl.textContent = ''; }, 4000);
}
function setBtn(btn, text, restore) {
  btn.textContent = text;
  if (restore) setTimeout(function() { btn.textContent = restore; }, 2000);
}
downloadBtn.addEventListener('click', function() {
  if (!navigator.share) { window.location.href = imageUrl; showMsg('Se abrió la imagen: mantené pulsado y elegí Guardar imagen.'); return; }
  downloadBtn.disabled = true;
  fetch(imageUrl).then(function(r) { return r.blob(); }).then(function(blob) {
    var file = new File([blob], 'figurita.jpg', { type: 'image/jpeg' });
    return navigator.share({ title: 'Mi figurita KCMundial', files: [file] });
  }).then(function() {
    setBtn(downloadBtn, '¡Listo!', 'Descargar');
  }).catch(function() {
    window.location.href = imageUrl;
    showMsg('Se abrió la imagen: mantené pulsado y elegí Guardar imagen.');
  }).finally(function() { downloadBtn.disabled = false; });
});
shareBtn.addEventListener('click', function() {
  var pageUrl = window.location.href;
  if (!navigator.share) { showMsg('Abrí esta página desde el celular (misma WiFi) para Compartir.'); return; }
  navigator.share({ title: 'Mi figurita KCMundial', url: pageUrl })
    .then(function() { setBtn(shareBtn, '¡Listo!', 'Compartir'); })
    .catch(function() { showMsg('Abrí esta página desde el celular en la misma WiFi.'); });
});
})();
</script>
</body>
</html>
""";
}
