import { DeliveryOrder, gateOutBadge, gateOutResolution } from '../../core/depot/depot-models';
import { lineFor, remainingSummary } from './gate-out-page';

describe('gate-out order helpers', () => {
  const order = {
    lines: [
      { category: 'Dry', size: 'Ft20', quantity: 3, releasedQuantity: 1, remainingQuantity: 2 },
      { category: 'Dry', size: 'Ft40', quantity: 1, releasedQuantity: 1, remainingQuantity: 0 },
      { category: 'Reefer', size: null, quantity: 2, releasedQuantity: 0, remainingQuantity: 2 },
    ],
  } as Pick<DeliveryOrder, 'lines'>;

  it('finds the line of the same type and size (Q1)', () => {
    expect(lineFor(order, { category: 'Dry', size: 'Ft40' })?.remainingQuantity).toBe(0);
    expect(lineFor(order, { category: 'Dry', size: 'Ft20' })?.remainingQuantity).toBe(2);
    expect(lineFor(order, { category: 'OpenTop', size: 'Ft20' })).toBeNull();
  });

  it('lets a line without size take both sizes', () => {
    expect(lineFor(order, { category: 'Reefer', size: 'Ft40' })?.quantity).toBe(2);
  });

  it('lists only what the order still needs', () => {
    expect(remainingSummary(order)).toBe('Dry 20ft còn 2/3 · Reefer 20/40ft còn 2/2');
  });
});

// Feedback 26/10: gate-out dropdown rows carry a chip like the gate-in dropdown
describe('gate-out dropdown chip', () => {
  it('is green when the container can leave under the order', () => {
    expect(gateOutBadge({ blockReason: null, repairStatus: null, condition: 'Normal' })).toEqual({
      text: 'Xuất được',
      tone: 'green',
    });
  });

  it('shows the repair step in red while a case is open', () => {
    expect(
      gateOutBadge({
        blockReason: 'RepairCase',
        repairStatus: 'AwaitingInspection',
        condition: 'Damaged',
      }).tone,
    ).toBe('red');
    expect(
      gateOutBadge({ blockReason: 'Damaged', repairStatus: null, condition: 'Damaged' }),
    ).toEqual({ text: 'Hư hỏng', tone: 'red' });
  });

  it('marks containers of the order that are not in the yard or under another one', () => {
    expect(
      gateOutBadge({ blockReason: 'NotInYard', repairStatus: null, condition: 'Normal' }),
    ).toEqual({ text: 'Không trong bãi', tone: 'grey' });
    expect(
      gateOutBadge({ blockReason: 'ContainerAbove', repairStatus: null, condition: 'Normal' }).text,
    ).toBe('Bị đè');
  });

  it('is red for a container out of the yard that still has damage or an open case', () => {
    expect(
      gateOutBadge({ blockReason: 'NotInYard', repairStatus: 'InRepair', condition: 'Damaged' }),
    ).toEqual({ text: 'Ngoài bãi · Đang sửa', tone: 'red' });
    expect(
      gateOutBadge({ blockReason: 'NotInYard', repairStatus: null, condition: 'Damaged' }).tone,
    ).toBe('red');
  });
});

// Feedback 26/10: a blocked container links to the screen that makes it available
describe('gate-out resolution link', () => {
  const base = {
    containerNumber: 'MSCU1234566',
    repairCaseId: null,
    aboveContainerNumber: null,
    situation: null,
    condition: 'Normal' as const,
  };

  it('has no link when the container can leave', () => {
    expect(gateOutResolution({ ...base, blockReason: null }, 'o1')).toBeNull();
  });

  it('opens the repair case with a way back to the order', () => {
    const r = gateOutResolution({ ...base, blockReason: 'RepairCase', repairCaseId: 'c1' }, 'o1');
    expect(r?.link).toEqual(['/repairs']);
    expect(r?.query).toEqual({ caseId: 'c1', backOrderId: 'o1' });
  });

  it('sends the container on top to the yard page', () => {
    const r = gateOutResolution(
      { ...base, blockReason: 'ContainerAbove', aboveContainerNumber: 'MSCU0000024' },
      'o1',
    );
    expect(r).toEqual({
      text: 'Đảo chuyển MSCU0000024 ra chỗ khác',
      link: ['/yard'],
      query: { container: 'MSCU0000024' },
    });
  });

  it('sends a clean container out of the yard to gate-in', () => {
    expect(gateOutResolution({ ...base, blockReason: 'NotInYard' }, 'o1')).toEqual({
      text: 'Nhập bãi container này',
      link: ['/gate-in'],
      query: { container: 'MSCU1234566' },
    });
  });

  it('sends a container out of the yard with an open case or damage to the repair page', () => {
    const atFacility = gateOutResolution(
      {
        ...base,
        blockReason: 'NotInYard',
        situation: {
          inYard: false,
          blockCode: null,
          bay: null,
          row: null,
          tier: null,
          gateInAt: null,
          lastGateOutAt: null,
          lastExitPurpose: null,
          openRepairCaseId: 'c1',
          openRepairStatus: 'InRepair',
          outsideRepairFacility: 'Xưởng Cát Lái',
          outsideRepairStatus: 'GatedOut',
        },
      },
      'o1',
    );
    expect(atFacility).toEqual({
      text: 'Mở hồ sơ sửa chữa (container đang ở Xưởng Cát Lái)',
      link: ['/repairs'],
      query: { caseId: 'c1', backOrderId: 'o1' },
    });
    const damaged = gateOutResolution(
      { ...base, blockReason: 'NotInYard', condition: 'Damaged' },
      'o1',
    );
    expect(damaged?.link).toEqual(['/repairs']);
    expect(damaged?.query).toEqual({ backOrderId: 'o1' });
  });
});
