import type {InstrumentCommand, Tank} from './api';

const ifxCommands: ReadonlySet<InstrumentCommand> = new Set([
  'MeasureI', 'MeasureII', 'MeasureIII', 'Stop', 'Hoist', 'Lower', 'Density'
]);

export function supportsIfxCommand(command: InstrumentCommand) {
  return ifxCommands.has(command);
}

export function applyIfxCapabilities(draft: Tank) {
  draft.controls.allowedCommands = draft.controls.allowedCommands.filter(supportsIfxCommand);
  draft.pressure1.enabled = false;
  draft.pressure2.enabled = false;
}
