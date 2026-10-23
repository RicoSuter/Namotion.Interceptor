/// <summary>
/// Controls page designed for video, as a companion of <see cref="StatusPage"/>: a brew button and the cup count,
/// polled from <c>/status</c> every 500 ms.
/// </summary>
public static class ControlsPage
{
    public const string Html = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>Controls</title>
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
            button {
              margin-top: 36px; font: inherit; font-size: 30px; font-weight: 600; color: #ffffff; background: #0a84ff;
              border: 0; border-radius: 999px; padding: 20px 40px; box-shadow: 0 4px 16px rgba(0, 0, 0, 0.35);
              transition: transform 0.15s ease, opacity 0.4s ease;
            }
            button:disabled { opacity: 0.4; }
            button:active { transform: scale(0.95); }
          </style>
        </head>
        <body>
          <main>
            <div class="kicker">Coffee machine</div>
            <h1>Controls</h1>
            <div class="card">
              <div class="row">
                <span class="label">Cups brewed</span>
                <span class="value" id="cups">--</span>
              </div>
            </div>
            <button id="brew" disabled>Brew Espresso</button>
          </main>
          <script>
            async function update() {
              try {
                const response = await fetch('/status');
                const machine = await response.json();
                document.getElementById('cups').textContent = machine.cupsBrewed;
                document.getElementById('brew').disabled = !machine.isReady;
                document.body.dataset.live = 'true';
              } catch {
                // The next poll retries.
              }
            }
            document.getElementById('brew').addEventListener('click', async () => {
              document.getElementById('brew').disabled = true;
              await fetch('/brew/Espresso', { method: 'POST' });
            });
            update();
            setInterval(update, 500);
          </script>
        </body>
        </html>
        """;
}
