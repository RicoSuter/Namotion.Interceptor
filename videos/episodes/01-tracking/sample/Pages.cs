/// <summary>
/// Pages designed for video: 800 by 760 columns with large type and the video palette, so two of them fit side by
/// side in one recording. The machine page polls <c>status</c>, the change stream page polls <c>stream</c>, both
/// every 250 ms.
/// </summary>
public static class Pages
{
    public static object Status(MachineHost host) => new
    {
        Method = host.BrewsInTransaction ? "BrewAsync" : "Brew",
        host.Machine.Status,
        host.Machine.IsReady,
        host.Machine.Boiler.Temperature,
        host.Machine.Boiler.TargetTemperature,
        host.Machine.Pump.Pressure,
        host.Machine.CupsBrewed,
        Recipes = host.Machine.Recipes.Keys.ToArray()
    };

    public static string Machine(MachineHost host) => MachineHtml.Replace("{{style}}", Style).Replace("{{id}}", host.Id);

    public static string Stream(MachineHost host) => StreamHtml.Replace("{{style}}", Style).Replace("{{id}}", host.Id);

    private const string Style = """
        :root { color-scheme: dark; }
        * { box-sizing: border-box; }
        body {
          margin: 0; height: 100vh; display: flex; align-items: center; justify-content: center; overflow: hidden;
          background: #1c1c1e; color: #f5f5f7; font-family: Inter, system-ui, sans-serif;
        }
        main { width: 704px; height: 680px; display: flex; flex-direction: column; }
        .top { display: flex; justify-content: space-between; align-items: center; height: 48px; }
        .kicker { display: flex; align-items: center; gap: 12px; font-size: 26px; font-weight: 600; color: #a1a1a6; }
        .kicker code { font-family: 'JetBrains Mono', monospace; color: #f5f5f7; }
        .dot { width: 14px; height: 14px; border-radius: 50%; background: #bf5af2; flex: none; }
        .pill {
          display: inline-flex; align-items: center; gap: 12px; padding: 10px 22px; border-radius: 999px;
          background: #2c2c2e; font-size: 24px; font-weight: 600; font-variant-numeric: tabular-nums;
          box-shadow: 0 4px 16px rgba(0, 0, 0, 0.35);
        }
        .pill code { font-family: 'JetBrains Mono', monospace; font-weight: 500; color: #a1a1a6; }
        .card {
          background: #2c2c2e; border-radius: 28px; padding: 22px 34px 26px; box-shadow: 0 12px 40px rgba(0, 0, 0, 0.45);
        }
        """;

    private const string MachineHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>Coffee Machine</title>
          <style>
            {{style}}
            h1 { margin: 18px 0 24px; font-size: 68px; font-weight: 700; letter-spacing: -0.03em; line-height: 1.05; white-space: nowrap; }
            .card { margin-bottom: 18px; }
            .row { display: flex; justify-content: space-between; align-items: baseline; }
            .label { font-size: 28px; font-weight: 500; color: #a1a1a6; }
            .value { font-size: 52px; font-weight: 600; font-variant-numeric: tabular-nums; }
            .bar { margin-top: 14px; height: 16px; border-radius: 8px; background: #3a3a3c; overflow: hidden; }
            .fill { height: 100%; width: 0; border-radius: 8px; transition: width 0.45s ease, background 0.6s ease; }
            #temperature-fill { background: #ff9f0a; }
            #pressure-fill { background: #64d2ff; }
            .ready #temperature-fill { background: #30d158; }
            #ready .state { width: 14px; height: 14px; border-radius: 50%; background: #636366; transition: background 0.4s ease; }
            .ready #ready .state { background: #30d158; }
            .bottom { display: flex; justify-content: space-between; align-items: center; margin-top: 8px; }
            .recipes { display: flex; gap: 14px; }
            button {
              font: inherit; font-size: 26px; font-weight: 600; color: #ffffff; background: #0a84ff; border: 0;
              border-radius: 999px; padding: 14px 28px; box-shadow: 0 4px 16px rgba(0, 0, 0, 0.35);
              transition: transform 0.15s ease, opacity 0.4s ease;
            }
            button.add { background: #3a3a3c; color: #f5f5f7; }
            button.new { animation: pop 0.5s ease; }
            button:active { transform: scale(0.95); }
            @keyframes pop { from { transform: scale(0.6); opacity: 0; } to { transform: scale(1); opacity: 1; } }
            #error {
              margin-top: 20px; min-height: 34px; font-size: 24px; font-weight: 600; color: #ff375f;
              opacity: 0; transition: opacity 0.3s ease; white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
            }
            #error.shown { opacity: 1; }
          </style>
        </head>
        <body>
          <main id="page">
            <div class="top">
              <div class="kicker"><span class="dot"></span><code id="method">Brew</code></div>
              <div class="pill" id="ready"><span class="state"></span><code>IsReady</code><b id="ready-value">false</b></div>
            </div>
            <h1 id="status">Connecting</h1>
            <div class="card">
              <div class="row"><span class="label">Boiler</span><span class="value" id="temperature">-- °C</span></div>
              <div class="bar"><div class="fill" id="temperature-fill"></div></div>
            </div>
            <div class="card">
              <div class="row"><span class="label">Pump</span><span class="value" id="pressure">-- bar</span></div>
              <div class="bar"><div class="fill" id="pressure-fill"></div></div>
            </div>
            <div class="bottom">
              <div class="recipes" id="recipes"></div>
              <div class="pill"><code>Cups</code><b id="cups">0</b></div>
            </div>
            <div id="error"></div>
          </main>
          <script>
            const base = '/{{id}}';
            let recipes = '';
            async function update() {
              try {
                const machine = await (await fetch(base + '/status')).json();
                document.getElementById('method').textContent = machine.method;
                document.getElementById('status').textContent = machine.status;
                document.getElementById('ready-value').textContent = machine.isReady ? 'true' : 'false';
                document.getElementById('temperature').textContent = machine.temperature.toFixed(1) + ' °C';
                const heat = Math.min(Math.max((machine.temperature - 20) / (machine.targetTemperature - 20), 0), 1);
                document.getElementById('temperature-fill').style.width = (heat * 100).toFixed(1) + '%';
                document.getElementById('pressure').textContent = machine.pressure.toFixed(1) + ' bar';
                document.getElementById('pressure-fill').style.width = (Math.min(machine.pressure / 9, 1) * 100).toFixed(1) + '%';
                document.getElementById('cups').textContent = machine.cupsBrewed;
                document.getElementById('page').classList.toggle('ready', machine.isReady);
                renderRecipes(machine.recipes);
                document.body.dataset.live = 'true';
              } catch {
                // The next poll retries.
              }
            }
            function renderRecipes(names) {
              const key = names.join(',');
              if (key === recipes) return;
              const known = recipes.split(',');
              recipes = key;
              const container = document.getElementById('recipes');
              container.replaceChildren(...names.map(name => {
                const button = document.createElement('button');
                button.id = 'brew-' + name.toLowerCase();
                button.textContent = name;
                button.onclick = () => brew(name);
                if (known.length > 1 && !known.includes(name)) button.classList.add('new');
                return button;
              }));
              if (!names.includes('Ristretto')) {
                const add = document.createElement('button');
                add.id = 'add-recipe';
                add.className = 'add';
                add.textContent = '+ Ristretto';
                add.onclick = addRecipe;
                container.append(add);
              }
            }
            async function brew(name) {
              const response = await fetch(base + '/brew/' + name, { method: 'POST' });
              showError(response.ok ? '' : (await response.text()).replace(/^"|"$/g, ''));
              update();
            }
            async function addRecipe() {
              await fetch(base + '/recipes', { method: 'POST' });
              update();
            }
            function showError(message) {
              const error = document.getElementById('error');
              if (message) error.textContent = message;
              error.classList.toggle('shown', !!message);
            }
            update();
            setInterval(update, 250);
          </script>
        </body>
        </html>
        """;

    private const string StreamHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>Change stream</title>
          <style>
            {{style}}
            .counters { display: flex; gap: 14px; margin: 18px 0 20px; }
            .counter { flex: 1; justify-content: space-between; padding: 12px 22px; }
            .counter code { font-size: 22px; }
            .counter b { font-size: 34px; font-weight: 700; }
            #entries { position: relative; flex: 1; overflow: hidden; }
            .entry {
              display: flex; flex-direction: column; gap: 2px; padding: 8px 22px; margin-bottom: 8px;
              background: #2c2c2e; border-radius: 20px; box-shadow: 0 4px 16px rgba(0, 0, 0, 0.3);
              animation: arrive 0.4s ease both; transition: background 1.2s ease;
            }
            .entry.fresh { background: #3a3a3c; }
            @keyframes arrive { from { transform: translateY(-24px); opacity: 0; } to { transform: none; opacity: 1; } }
            .name { display: flex; align-items: center; gap: 12px; font-size: 22px; font-weight: 600; color: #a1a1a6; }
            .name code { font-family: 'JetBrains Mono', monospace; font-size: 24px; color: #f5f5f7; font-weight: 600; }
            .values { font-family: 'JetBrains Mono', monospace; font-size: 24px; color: #f5f5f7; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
            .values .old { color: #a1a1a6; }
            .values .arrow { color: #636366; margin: 0 10px; }
            .lifecycle .dot { background: #30d158; }
          </style>
        </head>
        <body>
          <main>
            <div class="top">
              <div class="kicker"><span class="dot"></span>Change stream</div>
              <div class="kicker"><code>{{id}}</code></div>
            </div>
            <div class="counters">
              <div class="pill counter"><code>Temperature</code><b id="count-temperature">0</b></div>
              <div class="pill counter"><code>Status</code><b id="count-status">0</b></div>
              <div class="pill counter"><code>IsReady</code><b id="count-ready">0</b></div>
            </div>
            <div id="entries"></div>
          </main>
          <script>
            const base = '/{{id}}';
            // Subject of each property shown in the list, with its concept color from the video palette.
            const subjects = {
              TargetTemperature: ['Boiler', '#ff375f'], IsHot: ['Boiler', '#ff375f'],
              IsRunning: ['Pump', '#0a84ff'], IsLow: ['WaterTank', '#64d2ff']
            };
            let last = 0;
            async function update() {
              try {
                const stream = await (await fetch(base + '/stream')).json();
                document.getElementById('count-temperature').textContent = stream.counts.temperature;
                document.getElementById('count-status').textContent = stream.counts.status;
                document.getElementById('count-ready').textContent = stream.counts.isReady;
                const container = document.getElementById('entries');
                const newest = stream.entries.length > 0 ? stream.entries[0].sequence : 0;
                if (newest < last) {
                  container.replaceChildren();
                }
                const fresh = stream.entries.filter(entry => entry.sequence > last).reverse();
                fresh.forEach((entry, index) => container.prepend(render(entry, index)));
                while (container.children.length > 8) container.lastChild.remove();
                last = newest;
                document.body.dataset.live = 'true';
              } catch {
                // The next poll retries.
              }
            }
            function render(entry, index) {
              const element = document.createElement('div');
              element.className = 'entry fresh';
              element.id = 'entry-' + entry.sequence;
              element.dataset.property = entry.property;
              element.style.animationDelay = (index * 0.08) + 's';
              setTimeout(() => element.classList.remove('fresh'), 900);
              const name = document.createElement('div');
              name.className = 'name';
              const dot = document.createElement('span');
              dot.className = 'dot';
              const code = document.createElement('code');
              const values = document.createElement('div');
              values.className = 'values';
              if (entry.kind === 'change') {
                const [subject, color] = subjects[entry.property] ?? ['CoffeeMachine', '#bf5af2'];
                dot.style.background = color;
                code.textContent = entry.property;
                name.append(dot, subject + '.', code);
                values.innerHTML = '<span class="old"></span><span class="arrow">→</span><span class="new"></span>';
                values.querySelector('.old').textContent = entry.oldValue;
                values.querySelector('.new').textContent = entry.newValue;
              } else {
                element.classList.add('lifecycle');
                code.textContent = entry.kind;
                name.append(dot, 'Lifecycle ', code);
                values.textContent = entry.newValue;
              }
              element.append(name, values);
              return element;
            }
            update();
            setInterval(update, 250);
          </script>
        </body>
        </html>
        """;
}
