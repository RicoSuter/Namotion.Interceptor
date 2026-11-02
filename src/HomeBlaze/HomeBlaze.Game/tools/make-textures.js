// Draws the pastel textures of the ground and the walls and writes them as PNG files into the wwwroot/textures/ folders of
// HomeBlaze.Game and HomeBlaze.Game.Buildings.
// Usage: PLAYWRIGHT_PACKAGE=<.playwright/package of a built test project> node make-textures.js
// See README.md in this folder.
const fs = require('fs');
const path = require('path');

if (!process.env.PLAYWRIGHT_PACKAGE) {
    console.error('Usage: PLAYWRIGHT_PACKAGE=<path> node make-textures.js');
    process.exit(1);
}

const { chromium } = require(process.env.PLAYWRIGHT_PACKAGE);

const gameTextures = path.join(__dirname, '..', 'wwwroot', 'textures');
const buildingTextures = path.join(__dirname, '..', '..', 'HomeBlaze.Game.Buildings', 'wwwroot', 'textures');
const TEXTURES = [
    { kind: 'grass', folder: gameTextures },
    { kind: 'bricks', folder: buildingTextures },
    { kind: 'concrete', folder: buildingTextures },
    { kind: 'wood', folder: buildingTextures },
];

// Runs in the page. Every line is centered on the tile edge, so half of it lies outside and the neighboring repeat draws
// the other half: repeated tiles join to one line of the full width.
function draw(kind) {
    const size = 256;
    const canvas = document.createElement('canvas');
    canvas.width = size;
    canvas.height = size;
    const context = canvas.getContext('2d');
    const fill = { grass: '#9cc77f', bricks: '#e0866a', concrete: '#d9d4ca', wood: '#c99668' }[kind];
    const line = { grass: 'rgba(80,110,60,.35)', bricks: 'rgba(120,50,35,.45)', concrete: 'rgba(110,105,95,.4)', wood: 'rgba(95,60,30,.45)' }[kind];
    const stroke = (fromX, fromY, toX, toY) => {
        context.beginPath();
        context.moveTo(fromX, fromY);
        context.lineTo(toX, toY);
        context.stroke();
    };

    context.fillStyle = fill;
    context.fillRect(0, 0, size, size);
    context.strokeStyle = line;
    context.lineWidth = kind === 'grass' ? 6 : 5;
    context.lineCap = 'round';
    if (kind === 'bricks') {
        const rows = 8;
        for (let row = 0; row <= rows; row++) {
            stroke(0, row * size / rows, size, row * size / rows);
        }

        for (let row = 0; row < rows; row++) {
            for (let x = (row % 2) * size / 8; x < size + 1; x += size / 4) {
                stroke(x, row * size / rows, x, (row + 1) * size / rows);
            }
        }
    } else if (kind === 'wood') {
        for (let x = 0; x <= size; x += size / 4) {
            stroke(x, 0, x, size);
        }

        context.lineWidth = 2;
        for (let i = 0; i < 10; i++) {
            const x = (i * 53) % size;
            const y = (i * 97) % size;
            stroke(x + 8, y, x + 8, y + 30);
        }
    } else if (kind === 'concrete') {
        context.strokeRect(0, 0, size, size);
        context.fillStyle = line;
        for (let i = 0; i < 6; i++) {
            context.beginPath();
            context.arc((i * 71) % size, (i * 113) % size, 3, 0, 2 * Math.PI);
            context.fill();
        }
    } else {
        // The ground repeats once per meter, so the border is the meter grid.
        context.strokeRect(0, 0, size, size);
    }

    return canvas.toDataURL('image/png').split(',')[1];
}

(async () => {
    const browser = await chromium.launch();
    try {
        const page = await browser.newPage();
        for (const { kind, folder } of TEXTURES) {
            const png = Buffer.from(await page.evaluate(draw, kind), 'base64');
            const file = path.join(folder, kind + '.png');
            fs.writeFileSync(file, png);
            console.log(`${path.relative(process.cwd(), file)} ${png.length} bytes`);
        }
    } finally {
        await browser.close();
    }
})().catch(error => {
    console.error(error);
    process.exit(1);
});
