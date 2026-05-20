# INSDC XSD schemas

Mirror of <https://ftp.ebi.ac.uk/pub/databases/ena/doc/xsd/sra_1_5/>, downloaded once and committed for reproducible code generation via `dotnet xscgen`. Regenerate via the `regenerateInsdcTypes` FAKE target — do not edit `../Generated/` by hand.

## Local patches against upstream

| File              | Patch                                                                                              | Reason                                                                                                                                                                          |
| ----------------- | -------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `ENA.embl.xsd`    | Renamed local complexType `XrefType` → `EmblXrefType` (and all references within the same file).   | Collides with `SRA.common.xsd`'s `XRefType` under xscgen's PascalCase normalization. xscgen merges them into one class but emits inconsistent references; renaming disambiguates. |
