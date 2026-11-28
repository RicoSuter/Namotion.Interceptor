// A matter.js on/off light for integration tests:
// node virtual-light.mjs <storage directory> <port> <passcode> <discriminator>
// Prints "VIRTUAL-LIGHT-READY <manual pairing code>" once it is commissionable.
import { Endpoint, Environment, ServerNode, VendorId } from "@matter/main";
import { OnOffLightDevice } from "@matter/main/devices";

const [storagePath, port, passcode, discriminator] = process.argv.slice(2);
Environment.default.vars.set("storage.path", storagePath);

const server = await ServerNode.create({
    id: "virtual-light",
    network: { port: Number(port) },
    commissioning: { passcode: Number(passcode), discriminator: Number(discriminator) },
    productDescription: { name: "Virtual Light", deviceType: OnOffLightDevice.deviceType },
    basicInformation: {
        vendorName: "Namotion",
        vendorId: VendorId(0xfff1),
        nodeLabel: "Virtual Light",
        productName: "Virtual Light",
        productLabel: "Virtual Light",
        productId: 0x8000,
        serialNumber: "virtual-light-1",
        uniqueId: "virtual-light-unique-1",
    },
});

await server.add(new Endpoint(OnOffLightDevice, { id: "light" }));
await server.start();
console.log(`VIRTUAL-LIGHT-READY ${server.state.commissioning.pairingCodes.manualPairingCode}`);
