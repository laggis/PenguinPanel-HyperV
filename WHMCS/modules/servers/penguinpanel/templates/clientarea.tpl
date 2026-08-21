<style>
.pp-wrap{margin-top:18px}.pp-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(210px,1fr));gap:12px}.pp-card{border:1px solid rgba(127,127,127,.22);border-radius:12px;padding:15px;background:rgba(127,127,127,.04)}.pp-card h4{font-size:12px;text-transform:uppercase;letter-spacing:.05em;opacity:.65;margin:0 0 7px}.pp-value{font-size:17px;font-weight:600;word-break:break-word}.pp-muted{opacity:.68;font-size:13px}.pp-actions{display:flex;flex-wrap:wrap;gap:8px;margin-top:14px}.pp-alert{padding:11px 13px;border-radius:10px;margin-bottom:12px;background:rgba(220,53,69,.10);border:1px solid rgba(220,53,69,.25)}.pp-good{color:#198754}.pp-copy{cursor:pointer}.pp-secret{font-family:monospace;letter-spacing:.08em}.pp-heading{display:flex;justify-content:space-between;align-items:center;gap:10px;margin-bottom:12px}.pp-heading h3{margin:0}.pp-badge{padding:4px 9px;border-radius:999px;background:rgba(25,135,84,.12);font-size:12px;font-weight:700}
</style>
<div class="pp-wrap">
    <div class="pp-heading">
        <h3>PenguinPanel VPS</h3>
        <span class="pp-badge">{$ppVmState|escape}</span>
    </div>

    {if $ppApiError}
        <div class="pp-alert"><strong>Live status unavailable:</strong> {$ppApiError|escape}</div>
    {/if}
    {if $ppLastError}
        <div class="pp-alert"><strong>PenguinPanel:</strong> {$ppLastError|escape}</div>
    {/if}

    <div class="pp-grid">
        <div class="pp-card">
            <h4>Remote Desktop</h4>
            <div class="pp-value">{if $ppRdpEndpoint}{$ppRdpEndpoint|escape}{else}Not assigned yet{/if}</div>
            <div class="pp-muted">Public RDP endpoint</div>
        </div>
        <div class="pp-card">
            <h4>Windows Login</h4>
            <div class="pp-value">{$ppWindowsUsername|escape}</div>
            <div class="pp-muted">Username</div>
        </div>
        <div class="pp-card">
            <h4>Password</h4>
            <div class="pp-value pp-secret" id="pp-pass">••••••••••••</div>
            <div class="pp-muted">Initial password for Windows and a newly-created PenguinPanel account</div>
        </div>
        <div class="pp-card">
            <h4>PenguinPanel Account</h4>
            <div class="pp-value">{$ppPanelUsername|escape}</div>
            <div class="pp-muted">Panel username</div>
        </div>
        <div class="pp-card">
            <h4>vCPU</h4>
            <div class="pp-value">{$ppCpuCores|escape} Core{if $ppCpuCores != 1}s{/if}</div>
            <div class="pp-muted">Current WHMCS plan allocation</div>
        </div>
        <div class="pp-card">
            <h4>Memory</h4>
            <div class="pp-value">{$ppMemoryMb|escape} MB</div>
            <div class="pp-muted">Current WHMCS plan allocation</div>
        </div>
        <div class="pp-card">
            <h4>Disk</h4>
            <div class="pp-value">{$ppDiskGb|escape} GB</div>
            <div class="pp-muted">Virtual disk size</div>
        </div>
        {if $ppFiveMPort}
        <div class="pp-card">
            <h4>FiveM Public Port</h4>
            <div class="pp-value">{$ppFiveMPort|escape}</div>
            <div class="pp-muted">TCP + UDP mapping</div>
        </div>
        {/if}
        {if $ppInternalIp}
        <div class="pp-card">
            <h4>Private IPv4</h4>
            <div class="pp-value">{$ppInternalIp|escape}</div>
            <div class="pp-muted">Hyper-V internal network</div>
        </div>
        {/if}
    </div>

    <div class="pp-actions">
        <a class="btn btn-primary" href="{$ppPanelUrl|escape:'html'}" target="_blank" rel="noopener">Open PenguinPanel</a>
        {if $ppUpgradeUrl}<a class="btn btn-success" href="{$ppUpgradeUrl|escape:'html'}">Uppgradera / nedgradera VPS</a>{/if}
        {if $ppSupportUrl}<a class="btn btn-default" href="{$ppSupportUrl|escape:'html'}">Öppna supportärende</a>{/if}
        {if $ppRdpEndpoint}<button type="button" class="btn btn-default pp-copy" data-copy="{$ppRdpEndpoint|escape:'html'}">Copy RDP endpoint</button>{/if}
        <button type="button" class="btn btn-default" id="pp-show-password">Show password</button>
    </div>
</div>
<script>
(function(){
  var pass = {$ppServicePasswordJson nofilter};
  var passEl = document.getElementById('pp-pass');
  var show = document.getElementById('pp-show-password');
  var visible = false;
  if (show && passEl) {
    show.addEventListener('click', function(){
      visible = !visible;
      passEl.textContent = visible ? pass : '••••••••••••';
      show.textContent = visible ? 'Hide password' : 'Show password';
    });
  }
  Array.prototype.forEach.call(document.querySelectorAll('.pp-copy'), function(btn){
    btn.addEventListener('click', function(){
      var text = btn.getAttribute('data-copy') || '';
      if (navigator.clipboard && text) navigator.clipboard.writeText(text);
      var old = btn.textContent; btn.textContent = 'Copied';
      setTimeout(function(){ btn.textContent = old; }, 1200);
    });
  });
})();
</script>
