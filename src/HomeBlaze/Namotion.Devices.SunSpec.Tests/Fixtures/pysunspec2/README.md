# pysunspec2 Device Dumps

`inverter_123.json` and `device_1547.json` are test data of [pysunspec2](https://github.com/sunspec/pysunspec2/tree/8606a884c17a8d70d209dc4ef3898226687f03c5/sunspec2/tests/test_data), commit `8606a884c17a8d70d209dc4ef3898226687f03c5`, licensed under the Apache License 2.0 (see `LICENSE`). Values are raw register values, `null` means not implemented. In `device_1547.json` the entry with ID 713 uses an older layout that does not match the current model 713, so tests read only models 1 and 701 from it.
