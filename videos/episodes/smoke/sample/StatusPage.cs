/// <summary>
/// Status page designed for video: large type, the video palette, and a poll of <c>/status</c> every 500 ms. Sizes
/// follow the viewport width, so the page also fits one half of a split recording.
/// </summary>
public static class StatusPage
{
    public const string Html = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>Coffee Machine</title>
          <style>
            :root { color-scheme: dark; }
            * { box-sizing: border-box; }
            body {
              margin: 0; height: 100vh; display: flex; align-items: center; justify-content: center;
              background: #1c1c1e; color: #f5f5f7; font-family: Inter, system-ui, sans-serif;
            }
            main { width: min(920px, 88vw); }
            .kicker { font-size: 22px; font-weight: 600; letter-spacing: 0.24em; color: #a1a1a6; text-transform: uppercase; }
            h1 { margin: 16px 0 48px; font-size: min(104px, 8.2vw); font-weight: 700; letter-spacing: -0.03em; line-height: 1; }
            .card {
              background: #2c2c2e; border-radius: 28px; padding: 40px 48px;
              box-shadow: 0 12px 40px rgba(0, 0, 0, 0.45);
            }
            .row { display: flex; justify-content: space-between; align-items: baseline; }
            .label { font-size: 28px; font-weight: 500; color: #a1a1a6; }
            .value { font-size: min(64px, 5.6vw); font-weight: 600; font-variant-numeric: tabular-nums; }
            .bar { margin-top: 28px; height: 18px; border-radius: 9px; background: #3a3a3c; overflow: hidden; }
            .fill { height: 100%; width: 0; border-radius: 9px; background: #ff9f0a; transition: width 0.45s ease, background 0.6s ease; }
            .pill {
              display: inline-flex; align-items: center; gap: 14px; margin-top: 36px; padding: 14px 28px;
              border-radius: 999px; background: #2c2c2e; font-size: 28px; font-weight: 600;
              box-shadow: 0 4px 16px rgba(0, 0, 0, 0.35);
            }
            .dot { width: 16px; height: 16px; border-radius: 50%; background: #ff9f0a; transition: background 0.6s ease; }
            .ready .fill, .ready .dot { background: #30d158; }
          </style>
        </head>
        <body>
          <main id="page">
            <div class="kicker">Coffee machine</div>
            <h1 id="status">Connecting</h1>
            <div class="card">
              <div class="row">
                <span class="label">Boiler</span>
                <span class="value" id="temperature">-- °C</span>
              </div>
              <div class="bar"><div class="fill" id="fill"></div></div>
            </div>
            <div class="pill"><span class="dot"></span><span id="readiness">Heating up</span></div>
          </main>
          <script>
            async function update() {
              try {
                const response = await fetch('/status');
                const machine = await response.json();
                document.getElementById('status').textContent = machine.status;
                document.getElementById('temperature').textContent = machine.temperature.toFixed(1) + ' °C';
                const share = Math.min(Math.max((machine.temperature - 20) / (machine.targetTemperature - 20), 0), 1);
                document.getElementById('fill').style.width = (share * 100).toFixed(1) + '%';
                document.getElementById('readiness').textContent = machine.isReady ? 'Ready to brew' : 'Heating up';
                document.getElementById('page').classList.toggle('ready', machine.isReady);
                document.body.dataset.live = 'true';
              } catch {
                // The next poll retries.
              }
            }
            update();
            setInterval(update, 500);
          </script>
        </body>
        </html>
        """;
}
