// Types matching the API responses (enums are sent as strings by JsonStringEnumConverter)

export type ContainerSize = 'Ft20' | 'Ft40';
export type ContainerCategory =
  'Dry' | 'Reefer' | 'OpenTop' | 'FlatRack' | 'Bunker' | 'Ventilated' | 'Specialized';
export type ContainerCondition = 'Normal' | 'Damaged';

// Domain/Common/PagedResult
export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
  hasNextPage: boolean;
  hasPreviousPage: boolean;
}

// Partner (Đối tác): one catalog for container owners and line operators; the API path stays /api/line-operators
export type PartnerRole = 'Owner' | 'Operator';

export interface LineOperator {
  id: string;
  code: string;
  name: string;
  // Optional tax code (MST)
  taxCode: string | null;
  // Can own containers / can operate containers (assignments, visits, delivery orders)
  isOwner: boolean;
  isOperator: boolean;
  createdAt: string;
}

// Bay/Row/Tier sizes are null for a virtual block
export interface Block {
  id: string;
  code: string;
  description: string | null;
  isVirtual: boolean;
  maxBays: number | null;
  maxRows: number | null;
  maxTiers: number | null;
  // Container types the block takes (feedback 3); gate-in and move only offer blocks that take the type
  allowedCategories: ContainerCategory[];
  // Archived (soft-deleted) block: kept for history, never offered for a new position
  archivedAt: string | null;
  containersInYard: number;
  createdAt: string;
}

/*
  Catalog filters of the container list (GET /api/containers). Empty = no filter.
  operator: '' all, 'none' = no confirmed operating assignment today, otherwise the operator id.
  location: '' all, 'in' = in the yard, 'out' = not in the yard.
*/
export interface ContainerListFilters {
  ownerId: string;
  operator: string;
  category: ContainerCategory | '';
  size: ContainerSize | '';
  condition: ContainerCondition | '';
  location: '' | 'in' | 'out';
}

// Query parameters of the filters: only the ones set, with the API names
export function containerFilterParams(filters: ContainerListFilters): Record<string, string> {
  const params: Record<string, string> = {};
  if (filters.ownerId) params['ownerId'] = filters.ownerId;
  if (filters.operator === 'none') params['hasOperator'] = 'false';
  else if (filters.operator) params['operatorId'] = filters.operator;
  if (filters.category) params['category'] = filters.category;
  if (filters.size) params['size'] = filters.size;
  if (filters.condition) params['condition'] = filters.condition;
  if (filters.location) params['inYard'] = filters.location === 'in' ? 'true' : 'false';
  return params;
}

export interface Container {
  id: string;
  containerNumber: string;
  isoCode: string;
  size: ContainerSize;
  category: ContainerCategory;
  maxGrossWeightKg: number;
  tareWeightKg: number;
  manufactureDate: string | null;
  // Owner text as first recorded (before partners existed), kept so old data can be reconciled
  ownerName: string;
  condition: ContainerCondition;
  conditionNote: string | null;
  createdAt: string;
  // Owner partner; null until someone picks it in the catalog (shown as "chưa đối chiếu")
  ownerId: string | null;
  ownerCode: string | null;
  ownerPartnerName: string | null;
  // Line operator of the confirmed operating assignment covering today; null = gate-in is refused
  currentOperatorId: string | null;
  currentOperatorCode: string | null;
  // Where the container is now (filled by GET /api/containers and /api/containers/{id})
  situation?: ContainerSituation | null;
}

/*
  Feedback 23/10 (gate-in dropdown): the situation of a container, so the clerk sees before picking it
  whether it can come in and what blocks it. Same fields as ContainerSituation of the API.
*/
export interface ContainerSituation {
  inYard: boolean;
  // Open visit; bay/row/tier are null in a virtual block
  blockCode: string | null;
  bay: number | null;
  row: number | null;
  tier: number | null;
  gateInAt: string | null;
  // Last closed visit; null when it never came in
  lastGateOutAt: string | null;
  lastExitPurpose: GateOutPurpose | null;
  // Repair case not passed yet (it blocks release, not gate-in)
  openRepairCaseId: string | null;
  openRepairStatus: RepairCaseStatus | null;
  // Active outside repair: Requested = still in the yard, GatedOut = at the facility
  outsideRepairFacility: string | null;
  outsideRepairStatus: ExternalRepairStatus | null;
}

// Tones of the global .chip-* classes
export type ChipTone = 'green' | 'red' | 'amber' | 'indigo' | 'grey';

export interface StatusBadge {
  text: string;
  tone: ChipTone;
}

/*
  Badge of a container in the gate-in dropdown, in the order the API checks:
  already in the yard (refused), no confirmed operator today (refused), back from outside repair
  (accepted, linked to the transfer), otherwise accepted. Null when the API sent no situation.
*/
export function gateInBadge(container: Container): StatusBadge | null {
  const situation = container.situation;
  if (!situation) return null;
  if (situation.inYard) return { text: 'Đang trong bãi', tone: 'red' };
  if (!container.currentOperatorId) return { text: 'Chưa có hãng KT', tone: 'amber' };
  if (situation.outsideRepairStatus === 'GatedOut')
    return { text: 'Về từ sửa ngoài', tone: 'indigo' };
  return { text: 'Nhập được', tone: 'green' };
}

/*
  One line on where the container is: its position in the yard, the outside facility, its last exit
  or "never came in", then the open repair case. Empty when the API sent no situation.
*/
export function situationText(situation: ContainerSituation | null | undefined): string {
  if (!situation) return '';
  const parts: string[] = [];
  if (situation.inYard) {
    parts.push(
      `Tại ${formatPosition(situation.blockCode ?? '?', situation.bay, situation.row, situation.tier)}`,
    );
    if (situation.outsideRepairStatus === 'Requested') parts.push('chờ xuất đi sửa ngoài');
  } else if (situation.outsideRepairStatus === 'GatedOut') {
    parts.push(`Ở xưởng ${situation.outsideRepairFacility}`);
  } else if (situation.lastGateOutAt) {
    const purpose = situation.lastExitPurpose
      ? ` (${GATE_OUT_PURPOSE_LABEL[situation.lastExitPurpose]})`
      : '';
    parts.push(`Ra bãi ${formatDateTime(situation.lastGateOutAt)}${purpose}`);
  } else {
    parts.push('Chưa từng vào bãi');
  }
  if (situation.openRepairStatus)
    parts.push(`Sửa chữa: ${REPAIR_STATUS_LABEL[situation.openRepairStatus]}`);
  return parts.join(' · ');
}

// ---------- operating assignments (feedback 2: quan hệ khai thác) ----------

export type OperatingAssignmentStatus = 'Pending' | 'Confirmed' | 'Cancelled';

// Which line operator operates a container, from validFrom to validTo (Vietnam dates, both included)
export interface OperatingAssignment {
  id: string;
  containerId: string;
  containerNumber: string;
  operatorId: string;
  operatorCode: string;
  operatorName: string;
  validFrom: string;
  // null = open-ended
  validTo: string | null;
  status: OperatingAssignmentStatus;
  reference: string | null;
  note: string | null;
  createdAt: string;
  confirmedAt: string | null;
  endedAt: string | null;
  cancelledAt: string | null;
  cancelReason: string | null;
  // Visits that took their line operator from it: such an assignment can be ended but not cancelled
  visitCount: number;
}

// POST /api/containers/{id}/operating-assignments; confirm = already checked against the contract
export interface CreateOperatingAssignmentRequest {
  operatorId: string;
  validFrom: string;
  validTo: string | null;
  reference: string | null;
  note: string | null;
  confirm: boolean;
}

export const ASSIGNMENT_STATUS_LABEL: Record<OperatingAssignmentStatus, string> = {
  Pending: 'Chờ xác nhận',
  Confirmed: 'Đã xác nhận',
  Cancelled: 'Đã huỷ',
};

// "Chủ sở hữu, Hãng khai thác" for the partner list
export function partnerRolesText(partner: Pick<LineOperator, 'isOwner' | 'isOperator'>): string {
  const roles = [partner.isOwner ? 'Chủ sở hữu' : '', partner.isOperator ? 'Hãng khai thác' : ''];
  return roles.filter((r) => r).join(', ');
}

/*
  Feedback 23/10 (Khai thác was hard to test): the confirmed relation a new period would clash with, so the
  form can say which one before saving. Same rule as CheckNoConfirmedOverlapAsync: confirmed, and the two
  periods share at least one day (a null end = open-ended). Null when there is no clash.
*/
export function confirmedOverlap(
  assignments: readonly OperatingAssignment[],
  validFrom: string,
  validTo: string | null,
): OperatingAssignment | null {
  return (
    assignments.find(
      (a) =>
        a.status === 'Confirmed' &&
        (validTo === null || a.validFrom <= validTo) &&
        (a.validTo === null || a.validTo >= validFrom),
    ) ?? null
  );
}

/*
  Last day of the old relation when the container changes operator on `newFrom`: the day before.
  Null when that day is before the old start (the old relation would then be cancelled, not ended).
*/
export function handoverEndDate(
  old: Pick<OperatingAssignment, 'validFrom'>,
  newFrom: string,
): string | null {
  const lastDay = addDays(newFrom, -1);
  return lastDay >= old.validFrom ? lastDay : null;
}

// Same rule as OperatingAssignment.CoversDate: confirmed and the date inside the period ("yyyy-MM-dd" compare)
export function assignmentCovers(
  assignment: Pick<OperatingAssignment, 'status' | 'validFrom' | 'validTo'>,
  date: string,
): boolean {
  return (
    assignment.status === 'Confirmed' &&
    assignment.validFrom <= date &&
    (assignment.validTo === null || date <= assignment.validTo)
  );
}

// GET /api/container-visits/position
export interface ContainerPosition {
  visitId: string;
  containerId: string;
  containerNumber: string;
  size: ContainerSize;
  lineOperatorCode: string;
  blockId: string;
  blockCode: string;
  isVirtualBlock: boolean;
  bay: number | null;
  row: number | null;
  tier: number | null;
  gateInAt: string;
  isoCode: string;
  category: ContainerCategory;
  condition: ContainerCondition;
  // Open repair case of the container (feedback 5 markers); null when there is none
  repairCaseId: string | null;
  repairStatus: RepairCaseStatus | null;
  repairNote: string | null;
}

// GET /api/container-visits/in-yard and /yard-search
export interface YardContainer {
  visitId: string;
  containerId: string;
  containerNumber: string;
  isoCode: string;
  size: ContainerSize;
  category: ContainerCategory;
  condition: ContainerCondition;
  lineOperatorCode: string;
  blockId: string;
  blockCode: string;
  bay: number | null;
  row: number | null;
  tier: number | null;
  gateInAt: string;
  // Open repair case of the container (feedback 5 markers); null when there is none
  repairCaseId: string | null;
  repairStatus: RepairCaseStatus | null;
  repairNote: string | null;
}

/*
  Repair state filter of the yard search (feedback 5 keeps two marks apart):
  Abnormal = có bất thường, chờ giám định; InRepair = đã xác định cần sửa / đang xử lý.
*/
export type YardRepairState = 'None' | 'Abnormal' | 'InRepair' | 'AnyOpen';

export const YARD_REPAIR_STATE_LABEL: Record<YardRepairState, string> = {
  None: 'Không có hồ sơ',
  Abnormal: 'Bất thường – chờ giám định',
  InRepair: 'Cần sửa / đang xử lý',
  AnyOpen: 'Có hồ sơ đang mở',
};

// Filters of GET /api/container-visits/yard-search (feedback 8: Vị trí & đảo chuyển)
export interface YardSearchFilter {
  search?: string;
  blockId?: string | null;
  category?: ContainerCategory | null;
  size?: ContainerSize | null;
  operatorId?: string | null;
  repair?: YardRepairState | null;
  minDwellDays?: number | null;
  maxDwellDays?: number | null;
  // Feedback 8 (Tồn bãi): gate-in Vietnam dates "yyyy-MM-dd", both included
  gateInFrom?: string | null;
  gateInTo?: string | null;
  // Row order without a search: yard position (default), longest stay first, or newest first
  sort?: YardSort | null;
}

export type YardSort = 'Position' | 'Oldest' | 'Newest';

/*
  Mark drawn on a container in the yard: 'abnormal' (chờ giám định), 'repair' (cần sửa / đang xử lý), or null.
  A container marked Damaged without a case (data from before repair cases) counts as abnormal.
*/
export type RepairMarker = 'abnormal' | 'repair';

export function repairMarker(item: {
  repairStatus: RepairCaseStatus | null;
  condition: ContainerCondition;
}): RepairMarker | null {
  if (item.repairStatus === 'AwaitingInspection') return 'abnormal';
  if (item.repairStatus !== null && item.repairStatus !== 'Passed') return 'repair';
  return item.condition === 'Damaged' ? 'abnormal' : null;
}

export const REPAIR_MARKER_LABEL: Record<RepairMarker, string> = {
  abnormal: 'Bất thường – chờ giám định',
  repair: 'Cần sửa / đang xử lý',
};

// GET /api/container-visits/daily
export interface DailyGateIn {
  visitId: string;
  containerNumber: string;
  isoCode: string;
  size: ContainerSize;
  category: ContainerCategory;
  lineOperatorCode: string;
  blockCode: string;
  bay: number | null;
  row: number | null;
  tier: number | null;
  // Where the container was put at gate-in; null for visits recorded before this was stored
  gateInBlockCode: string | null;
  gateInBay: number | null;
  gateInRow: number | null;
  gateInTier: number | null;
  vehicleNo: string;
  gateInAt: string;
}

export interface DailyGateOut {
  visitId: string;
  containerNumber: string;
  isoCode: string;
  size: ContainerSize;
  category: ContainerCategory;
  lineOperatorCode: string;
  deliveryOrderNumber: string | null;
  orderLineQuantity: number | null;
  orderLineRemaining: number | null;
  vehicleNo: string | null;
  gateOutAt: string;
  // EmptyRelease = cấp rỗng under an order; ExternalRepair = chuyển sửa ngoài (no order, repairFacility set)
  exitPurpose: GateOutPurpose | null;
  repairFacility: string | null;
}

export interface DailyVisits {
  date: string;
  gateIns: DailyGateIn[];
  gateOuts: DailyGateOut[];
}

// POST /api/container-visits/gate-in (GateInCommand)
export interface GateInRequest {
  containerId: string;
  lineOperatorId: string;
  blockId: string;
  bay: number | null;
  row: number | null;
  tier: number | null;
  condition: ContainerCondition;
  conditionNote: string | null;
  vehicleNo: string;
}

export interface GateInResult {
  visitId: string;
  containerNumber: string;
  blockCode: string;
  bay: number | null;
  row: number | null;
  tier: number | null;
  gateInAt: string;
  // Set when the container came in Damaged, or came back from outside repair: the repair case it belongs to
  repairCaseId: string | null;
  // True when this gate-in closed a transfer to an outside repair facility (the case still needs acceptance)
  returnedFromRepair: boolean;
}

// POST /api/container-visits/move (MoveContainerCommand); the response is a ContainerPosition
export interface MoveRequest {
  containerId: string;
  blockId: string;
  bay: number | null;
  row: number | null;
  tier: number | null;
  // Where the screen saw the container: the API answers 409 PositionChanged if it moved meanwhile
  fromBlockId?: string;
  fromBay?: number | null;
  fromRow?: number | null;
  fromTier?: number | null;
}
export interface Customer {
  id: string;
  taxCode: string;
  name: string;
  createdAt: string;
}

// GET /api/delivery-orders(/{id}): quantity ordered, released at gate-out and still available per type + size
export interface DeliveryOrderLine {
  category: ContainerCategory;
  // Q1: one line per type and size; null only on lines created before sizes existed (both sizes)
  size: ContainerSize | null;
  quantity: number;
  releasedQuantity: number;
  remainingQuantity: number;
}

export interface DeliveryOrder {
  id: string;
  orderNumber: string;
  lineOperatorId: string;
  lineOperatorCode: string;
  customerId: string;
  customerTaxCode: string;
  customerName: string;
  // Vietnam date "yyyy-MM-dd": the last day containers may be released
  expiryDate: string;
  // Computed by the API with today's Vietnam date
  isExpired: boolean;
  vesselVoyage: string;
  lines: DeliveryOrderLine[];
  createdAt: string;
  // Computed by the API (feedback 6): Chưa giao / Đang giao / Đã giao đủ / Hết hạn / Đã huỷ
  status: DeliveryOrderStatus;
  cancelledAt: string | null;
  cancelReason: string | null;
}

// Filters of GET /api/delivery-orders; usable = only orders a container can still leave under
export interface DeliveryOrderFilter {
  search?: string;
  status?: DeliveryOrderStatus | null;
  usable?: boolean;
  operatorId?: string | null;
}

// POST /api/delivery-orders (CreateDeliveryOrderCommand)
export interface CreateDeliveryOrderRequest {
  orderNumber: string;
  lineOperatorId: string;
  customerId: string;
  expiryDate: string;
  vesselVoyage: string;
  lines: { category: ContainerCategory; size: ContainerSize; quantity: number }[];
}

export interface CreateDeliveryOrderResult {
  id: string;
  orderNumber: string;
}

// POST /api/container-visits/gate-out (GateOutCommand)
export interface GateOutRequest {
  containerId: string;
  deliveryOrderId: string;
  vehicleNo: string;
}

export interface GateOutResult {
  visitId: string;
  containerNumber: string;
  orderNumber: string;
  category: ContainerCategory;
  // Containers of this type and size still allowed under the order after this release
  remainingQuantity: number;
  vehicleNo: string;
  gateOutAt: string;
  size: ContainerSize;
}

// Why a container of the order cannot leave yet; null = it can
export type GateOutBlockReason = 'NotInYard' | 'RepairCase' | 'Damaged' | 'ContainerAbove';

export const GATE_OUT_BLOCK_LABEL: Record<GateOutBlockReason, string> = {
  NotInYard: 'Không ở trong bãi',
  RepairCase: 'Đang có hồ sơ giám định/sửa chữa',
  Damaged: 'Đang hư hỏng',
  ContainerAbove: 'Có container xếp phía trên',
};

/*
  GET /api/container-visits/gate-out-candidates?deliveryOrderId= (feedback 6 + 26/10): the containers of the
  order (its line operator, a type + size it still needs), in the yard or not. The ones that can leave come
  first; the others carry the reason. visitId / blockCode / gateInAt are null out of the yard.
*/
export interface GateOutCandidate {
  visitId: string | null;
  containerId: string;
  containerNumber: string;
  isoCode: string;
  size: ContainerSize;
  category: ContainerCategory;
  blockCode: string | null;
  bay: number | null;
  row: number | null;
  tier: number | null;
  condition: ContainerCondition;
  gateInAt: string | null;
  blockReason: GateOutBlockReason | null;
  repairCaseId: string | null;
  repairStatus: RepairCaseStatus | null;
  aboveContainerNumber: string | null;
  // Same "where is it" data as the gate-in dropdown (position, last exit, outside facility, repair step)
  situation: ContainerSituation | null;
  eligible: boolean;
}

/*
  Chip of a row in the gate-out dropdown (same look as gate-in): green when it can leave under the order,
  red when repair or damage holds it, amber when another container rests on it, grey when it is not in the yard.
*/
export function gateOutBadge(
  candidate: Pick<GateOutCandidate, 'blockReason' | 'repairStatus' | 'condition'>,
): StatusBadge {
  switch (candidate.blockReason) {
    case null:
      return { text: 'Xuất được', tone: 'green' };
    case 'NotInYard':
      // Out of the yard with an open case or recorded damage: red, because it needs the repair page first
      if (candidate.repairStatus)
        return { text: `Ngoài bãi · ${REPAIR_STATUS_LABEL[candidate.repairStatus]}`, tone: 'red' };
      if (candidate.condition === 'Damaged') return { text: 'Ngoài bãi · Hư hỏng', tone: 'red' };
      return { text: 'Không trong bãi', tone: 'grey' };
    case 'RepairCase':
      return {
        text: candidate.repairStatus
          ? REPAIR_STATUS_LABEL[candidate.repairStatus]
          : 'Đang sửa chữa',
        tone: 'red',
      };
    case 'Damaged':
      return { text: 'Hư hỏng', tone: 'red' };
    case 'ContainerAbove':
      return { text: 'Bị đè', tone: 'amber' };
  }
}

// A link to the screen that makes a blocked container available (router commands + query params)
export interface GateOutResolution {
  text: string;
  link: string[];
  query: Record<string, string>;
}

/*
  Feedback 26/10: a container of the order that cannot leave yet gets a link to the screen that resolves it:
  - repair case / damaged: the repair page (back link to the order);
  - another container on top: the yard page with that container picked, to move it away;
  - not in the yard but with an open case (for example at the outside facility) or recorded damage:
    the repair page too, since the case decides what happens next;
  - not in the yard and clean (released or never came in): the gate-in page with this container picked.
  Null when the container can leave.
*/
export function gateOutResolution(
  candidate: Pick<
    GateOutCandidate,
    | 'blockReason'
    | 'containerNumber'
    | 'repairCaseId'
    | 'aboveContainerNumber'
    | 'situation'
    | 'condition'
  >,
  orderId: string,
): GateOutResolution | null {
  switch (candidate.blockReason) {
    case null:
      return null;
    case 'RepairCase':
      return {
        text: 'Mở hồ sơ giám định/sửa chữa',
        link: ['/repairs'],
        query: candidate.repairCaseId
          ? { caseId: candidate.repairCaseId, backOrderId: orderId }
          : { backOrderId: orderId },
      };
    case 'Damaged':
      return {
        text: 'Mở trang Giám định & sửa chữa',
        link: ['/repairs'],
        query: { backOrderId: orderId },
      };
    case 'ContainerAbove':
      return candidate.aboveContainerNumber
        ? {
            text: `Đảo chuyển ${candidate.aboveContainerNumber} ra chỗ khác`,
            link: ['/yard'],
            query: { container: candidate.aboveContainerNumber },
          }
        : { text: 'Mở trang Vị trí & đảo chuyển', link: ['/yard'], query: {} };
    case 'NotInYard': {
      // Feedback 26/10: damage or an open case goes to the repair page even when the container is outside
      const caseId = candidate.repairCaseId ?? candidate.situation?.openRepairCaseId ?? null;
      const facility =
        candidate.situation?.outsideRepairStatus === 'GatedOut'
          ? candidate.situation.outsideRepairFacility
          : null;
      if (caseId) {
        return {
          text: facility
            ? `Mở hồ sơ sửa chữa (container đang ở ${facility})`
            : 'Mở hồ sơ giám định/sửa chữa',
          link: ['/repairs'],
          query: { caseId, backOrderId: orderId },
        };
      }
      if (candidate.condition === 'Damaged') {
        return {
          text: 'Mở trang Giám định & sửa chữa',
          link: ['/repairs'],
          query: { backOrderId: orderId },
        };
      }
      // Clean and outside: it has to come back through the gate first
      return {
        text: 'Nhập bãi container này',
        link: ['/gate-in'],
        query: { container: candidate.containerNumber },
      };
    }
  }
}

// Why a container left the yard: cấp rỗng under an order, or chuyển sửa ngoài
export type GateOutPurpose = 'EmptyRelease' | 'ExternalRepair';

export const GATE_OUT_PURPOSE_LABEL: Record<GateOutPurpose, string> = {
  EmptyRelease: 'Cấp rỗng',
  ExternalRepair: 'Chuyển sửa ngoài',
};

// POST /api/container-visits/gate-out-repair
export interface GateOutForRepairRequest {
  transferId: string;
  vehicleNo: string;
}

export interface GateOutForRepairResult {
  visitId: string;
  containerNumber: string;
  facility: string;
  vehicleNo: string;
  gateOutAt: string;
}

// GET /api/statistics/yard-inventory: shortStay = fewer than longStayDays days, longStay = that many or more
export interface LineOperatorInventory {
  lineOperatorId: string;
  code: string;
  name: string;
  shortStay: number;
  longStay: number;
  total: number;
}

export interface YardInventory {
  // Vietnam date and exact time the dwell days were counted on (feedback 7: hiển thị mốc tính)
  asOf: string;
  asOfTime: string;
  longStayDays: number;
  lineOperators: LineOperatorInventory[];
  totalShortStay: number;
  totalLongStay: number;
  total: number;
}

// GET /api/statistics/throughput: one row per Vietnam date and line operator with at least one move
export interface DailyThroughput {
  date: string;
  lineOperatorId: string;
  lineOperatorCode: string;
  gateIn: number;
  gateOut: number;
}

export interface Throughput {
  from: string;
  to: string;
  days: DailyThroughput[];
  totalGateIn: number;
  // totalGateOut = totalReleased (cấp rỗng) + totalToRepair (chuyển sửa ngoài)
  totalGateOut: number;
  totalReleased: number;
  totalToRepair: number;
  // One row per line operator with at least one event: the rows the detail tabs open from
  lineOperators: OperatorThroughput[];
}

export interface OperatorThroughput {
  lineOperatorId: string;
  code: string;
  name: string;
  gateIn: number;
  gateOut: number;
  released: number;
  toRepair: number;
}

// Same limit as GetThroughputQuery.MaxDays (both dates included)
export const THROUGHPUT_MAX_DAYS = 366;

/*
  Filters of the yard inventory report: the same ones as the detail rows (yard search), so a total and
  the rows opened under it always agree (feedback 8). Empty values are left out of the request.
*/
export type YardInventoryFilter = Omit<YardSearchFilter, 'search' | 'sort'>;

// Filters of the throughput totals and detail rows (purpose only narrows gate-outs)
export interface ThroughputFilter {
  lineOperatorId?: string | null;
  category?: ContainerCategory | null;
  size?: ContainerSize | null;
  purpose?: GateOutPurpose | null;
}

export type ThroughputEventKind = 'GateIn' | 'GateOut';

// ---------- container history (feedback 7: Lịch sử container) ----------

/*
  One visit (lượt). visitRef is a short code of the visit (first 8 characters of its id).
  ownerCode / ownerName: owner recorded at gate-in; null = no historical data, never today's owner.
  lineOperatorCode: operator of that visit. dwellDays: to the gate-out, or to today for an open visit.
*/
export interface VisitHistoryRow {
  visitId: string;
  visitRef: string;
  containerId: string;
  containerNumber: string;
  isoCode: string;
  category: ContainerCategory;
  size: ContainerSize;
  ownerCode: string | null;
  ownerName: string | null;
  lineOperatorId: string;
  lineOperatorCode: string;
  gateInAt: string;
  gateOutAt: string | null;
  isOpen: boolean;
  dwellDays: number;
  exitPurpose: GateOutPurpose | null;
  deliveryOrderId: string | null;
  deliveryOrderNumber: string | null;
  repairFacility: string | null;
}

// Which time the period applies to: gate-in, gate-out, or either (still one row per visit)
export type VisitHistoryEvent = 'Any' | 'GateIn' | 'GateOut';

export const VISIT_HISTORY_EVENT_LABEL: Record<VisitHistoryEvent, string> = {
  Any: 'Nhập hoặc xuất',
  GateIn: 'Nhập (giờ vào)',
  GateOut: 'Xuất (giờ ra)',
};

export type VisitState = 'Open' | 'Closed';

export const VISIT_STATE_LABEL: Record<VisitState, string> = {
  Open: 'Đang mở (trong bãi)',
  Closed: 'Đã đóng (đã xuất)',
};

// Filters of GET /api/container-visits/history; also the query params of the history page
export interface VisitHistoryFilter {
  search?: string | null;
  containerId?: string | null;
  operatorId?: string | null;
  from?: string | null;
  to?: string | null;
  event?: VisitHistoryEvent | null;
  category?: ContainerCategory | null;
  size?: ContainerSize | null;
  state?: VisitState | null;
  purpose?: GateOutPurpose | null;
}

export interface VisitPosition {
  blockId: string;
  blockCode: string;
  isVirtual: boolean;
  bay: number | null;
  row: number | null;
  tier: number | null;
}

export type VisitDocumentKind =
  | 'OperatingAssignment'
  | 'DeliveryOrder'
  | 'ExternalRepairOut'
  | 'ExternalRepairReturn'
  | 'RepairPhoto';

export const VISIT_DOCUMENT_KIND_LABEL: Record<VisitDocumentKind, string> = {
  OperatingAssignment: 'Căn cứ phân công khai thác',
  DeliveryOrder: 'Lệnh cấp rỗng',
  ExternalRepairOut: 'Chỉ thị chuyển sửa ngoài',
  ExternalRepairReturn: 'Quay về từ sửa ngoài',
  RepairPhoto: 'Ảnh hư hỏng',
};

// GET /api/container-visits/history/{visitId}
export interface VisitHistoryDetail {
  visit: VisitHistoryRow;
  gateIn: {
    at: string;
    vehicleNo: string;
    condition: ContainerCondition;
    conditionNote: string | null;
    legacyGrade: string | null;
    // null: gate-in position not stored (visits before 23/10/2026)
    position: VisitPosition | null;
  };
  gateOut: {
    at: string;
    vehicleNo: string;
    purpose: GateOutPurpose | null;
    deliveryOrderNumber: string | null;
    customerName: string | null;
    repairFacility: string | null;
  } | null;
  currentPosition: VisitPosition | null;
  moves: { movedAt: string; from: VisitPosition; to: VisitPosition; movedBy: string | null }[];
  repairCases: {
    id: string;
    status: RepairCaseStatus;
    repairNeeded: boolean | null;
    openedAt: string;
    closedAt: string | null;
    firstFinding: string | null;
    photoCount: number;
  }[];
  documents: {
    kind: VisitDocumentKind;
    reference: string | null;
    at: string | null;
    linkId: string | null;
  }[];
}

export type ContainerPresence = 'NeverIn' | 'InDepot' | 'AtOutsideRepair' | 'OutOfDepot';

export const CONTAINER_PRESENCE_LABEL: Record<ContainerPresence, string> = {
  NeverIn: 'Chưa từng vào depot',
  InDepot: 'Đang trong depot',
  AtOutsideRepair: 'Đang sửa ngoài',
  OutOfDepot: 'Ngoài depot',
};

// GET /api/container-visits/history/containers/{containerId}
export interface ContainerVisitSummary {
  containerId: string;
  containerNumber: string;
  isoCode: string;
  category: ContainerCategory;
  size: ContainerSize;
  ownerCode: string | null;
  ownerName: string | null;
  gateInCount: number;
  gateOutCount: number;
  presence: ContainerPresence;
  currentVisitId: string | null;
  currentDwellDays: number | null;
  // Sum of the dwell days of every visit; days outside the depot are not added
  totalDwellDays: number;
  asOf: string;
}

/*
  Query string values without the empty ones (null, undefined, '' or blank text), so a filter left empty
  is not sent at all. Text is trimmed; numbers are kept, 0 included.
*/
export function compactParams(
  values: Record<string, string | number | null | undefined>,
): Record<string, string | number> {
  const params: Record<string, string | number> = {};
  for (const [key, value] of Object.entries(values)) {
    if (value === null || value === undefined) continue;
    if (typeof value === 'string') {
      if (value.trim()) params[key] = value.trim();
    } else {
      params[key] = value;
    }
  }
  return params;
}

/*
  Query params of a link from a report to the history page (feedback 7: báo cáo liên kết sang lịch sử mang
  theo bộ lọc). Same names as the history page reads; empty filters are left out.
*/
export function historyLinkParams(filter: VisitHistoryFilter): Record<string, string | number> {
  return compactParams({
    search: filter.search,
    containerId: filter.containerId,
    operatorId: filter.operatorId,
    from: filter.from,
    to: filter.to,
    event: filter.event,
    category: filter.category,
    size: filter.size,
    state: filter.state,
    purpose: filter.purpose,
  });
}

/*
  History filter read back from the page URL (links from reports and the container catalog, reload, back).
  Values that are not one of the API's own (a mistyped or old link) are dropped instead of sent, so the
  page still opens with the remaining filters.
*/
export function historyFilterFromParams(get: (key: string) => string | null): VisitHistoryFilter {
  const pick = <T extends string>(key: string, allowed: readonly T[]): T | null => {
    const value = get(key);
    return value !== null && (allowed as readonly string[]).includes(value) ? (value as T) : null;
  };
  const date = (key: string): string | null => {
    const value = get(key);
    return value && /^\d{4}-\d{2}-\d{2}$/.test(value) ? value : null;
  };
  return {
    search: get('search'),
    containerId: get('containerId'),
    operatorId: get('operatorId'),
    from: date('from'),
    to: date('to'),
    event: pick<VisitHistoryEvent>('event', ['Any', 'GateIn', 'GateOut']),
    category: pick('category', CATEGORIES),
    size: pick('size', SIZES),
    state: pick<VisitState>('state', ['Open', 'Closed']),
    purpose: pick('purpose', GATE_OUT_PURPOSES),
  };
}

// "Cấp rỗng · DO-001" / "Chuyển sửa ngoài · Xưởng A"; "" while the visit is open
export function exitText(
  row: Pick<VisitHistoryRow, 'exitPurpose' | 'deliveryOrderNumber' | 'repairFacility'>,
): string {
  if (!row.exitPurpose) return '';
  const reference =
    row.exitPurpose === 'EmptyRelease' ? row.deliveryOrderNumber : row.repairFacility;
  return reference
    ? `${GATE_OUT_PURPOSE_LABEL[row.exitPurpose]} · ${reference}`
    : GATE_OUT_PURPOSE_LABEL[row.exitPurpose];
}

// ---------- catalog requests (Danh mục) ----------

// PUT /api/line-operators/{id}: name, tax code and roles; the code never changes. At least one role.
export interface UpdateLineOperatorRequest {
  name: string;
  taxCode: string | null;
  isOwner: boolean;
  isOperator: boolean;
}

// POST /api/line-operators
export interface CreateLineOperatorRequest extends UpdateLineOperatorRequest {
  code: string;
}

// POST /api/customers; PUT /api/customers/{id} only renames ({ name }), the tax code never changes
export interface CreateCustomerRequest {
  taxCode: string;
  name: string;
}

// POST /api/blocks: physical block = all three sizes, virtual block = none; at least one container type
export interface CreateBlockRequest {
  code: string;
  description: string | null;
  isVirtual: boolean;
  maxBays: number | null;
  maxRows: number | null;
  maxTiers: number | null;
  allowedCategories: ContainerCategory[];
}

// PUT /api/blocks/{id}: code, description and accepted types (the size has its own endpoint)
export interface UpdateBlockRequest {
  code: string;
  description: string | null;
  allowedCategories: ContainerCategory[];
}

// DELETE /api/blocks/{id}: archived = true when the block had history and was kept (soft delete)
export interface DeleteBlockResult {
  id: string;
  code: string;
  archived: boolean;
}

// PUT /api/blocks/{id}/size (physical blocks only; 409 when a container would be left outside)
export interface ResizeBlockRequest {
  maxBays: number;
  maxRows: number;
  maxTiers: number;
}

// PUT /api/containers/{id}: every field of the master data except the container number
export interface UpdateContainerRequest {
  isoCode: string;
  size: ContainerSize;
  category: ContainerCategory;
  maxGrossWeightKg: number;
  tareWeightKg: number;
  // "yyyy-MM-dd" or null when unknown
  manufactureDate: string | null;
  // A partner with the owner role, picked from the catalog (no free text)
  ownerId: string;
}

// POST /api/containers
export interface CreateContainerRequest extends UpdateContainerRequest {
  containerNumber: string;
}

// The 7 container types of Appendix II, in the order of the API enum
export const CATEGORIES: ContainerCategory[] = [
  'Dry',
  'Reefer',
  'OpenTop',
  'FlatRack',
  'Bunker',
  'Ventilated',
  'Specialized',
];

// Both sizes, in the order of the API enum (filters and dropdowns)
export const SIZES: ContainerSize[] = ['Ft20', 'Ft40'];

// Repair states offered in the filters, most urgent first
export const YARD_REPAIR_STATES: YardRepairState[] = ['Abnormal', 'InRepair', 'AnyOpen', 'None'];

export const GATE_OUT_PURPOSES: GateOutPurpose[] = ['EmptyRelease', 'ExternalRepair'];

// ---------- display helpers ----------
export const SIZE_LABEL: Record<ContainerSize, string> = { Ft20: '20ft', Ft40: '40ft' };

// Container types of Appendix II, shown with their usual English names (as in the spec)
export const CATEGORY_LABEL: Record<ContainerCategory, string> = {
  Dry: 'Dry',
  Reefer: 'Reefer',
  OpenTop: 'Open Top',
  FlatRack: 'Flat Rack',
  Bunker: 'Bunker',
  Ventilated: 'Ventilated',
  Specialized: 'Specialized',
};
// Vietnamese hint next to each type, so staff who know the local name can find it
export const CATEGORY_HINT: Record<ContainerCategory, string> = {
  Dry: 'Container khô',
  Reefer: 'Container lạnh',
  OpenTop: 'Container mở nóc',
  FlatRack: 'Container mặt bằng',
  Bunker: 'Container bồn',
  Ventilated: 'Container thông gió',
  Specialized: 'Container chuyên dụng',
};

export const CONDITION_LABEL: Record<ContainerCondition, string> = {
  Normal: 'Bình thường',
  Damaged: 'Hư hỏng',
};

// Same check as Block.CheckAccepts on the API: the block's list of types, never its name or description
export function blockAccepts(
  block: Pick<Block, 'allowedCategories'>,
  category: ContainerCategory,
): boolean {
  return block.allowedCategories.includes(category);
}

// "Dry, Open Top" in the enum order; "Tất cả loại" when the block takes every type
export function categoriesText(categories: readonly ContainerCategory[]): string {
  if (CATEGORIES.every((c) => categories.includes(c))) return 'Tất cả loại';
  return CATEGORIES.filter((c) => categories.includes(c))
    .map((c) => CATEGORY_LABEL[c])
    .join(', ');
}

// "A 04-1-1" for a physical block, just "WAIT" for a virtual block (same style as the mockups)
export function formatPosition(
  blockCode: string,
  bay: number | null,
  row: number | null,
  tier: number | null,
): string {
  return bay === null ? blockCode : `${blockCode} ${String(bay).padStart(2, '0')}-${row}-${tier}`;
}

// Vietnam is UTC+7 all year (no daylight saving), same offset as Domain/Services/DepotTime
const VIETNAM_OFFSET_MS = 7 * 60 * 60 * 1000;
const DAY_MS = 24 * 60 * 60 * 1000;

// Day number of an instant in Vietnam time: shift to UTC+7, then drop the time of day
function vietnamDay(ms: number): number {
  return Math.floor((ms + VIETNAM_OFFSET_MS) / DAY_MS);
}

// Days in the yard counted in Vietnam calendar days, gate-in day = 0 (same as DepotTime.DwellDays)
export function dwellDays(gateInAt: string, now: Date = new Date()): number {
  return vietnamDay(now.getTime()) - vietnamDay(new Date(gateInAt).getTime());
}

// Today's date in Vietnam as "yyyy-MM-dd" (same as DepotTime.DateOf), for date inputs and expiry checks;
// the browser's own time zone is not used, so a laptop set to another zone still shows the depot's date
export function vietnamToday(now: Date = new Date()): string {
  return new Date(vietnamDay(now.getTime()) * DAY_MS).toISOString().slice(0, 10);
}

// "2026-10-31" -> "31/10/2026" (a DateOnly from the API has no time, so no time zone conversion)
export function formatDate(isoDate: string): string {
  const [year, month, day] = isoDate.split('-');
  return `${day}/${month}/${year}`;
}

// Instant from the API -> "22/10/2026 08:20" in Vietnam time (for text built in code; templates use DatePipe)
export function formatDateTime(iso: string): string {
  const vietnam = new Date(new Date(iso).getTime() + VIETNAM_OFFSET_MS).toISOString();
  return `${formatDate(vietnam.slice(0, 10))} ${vietnam.slice(11, 16)}`;
}

// "2026-10-22" + 3 -> "2026-10-25"; plain calendar arithmetic on a date without time (UTC avoids DST shifts)
export function addDays(isoDate: string, days: number): string {
  return new Date(Date.parse(`${isoDate}T00:00:00Z`) + days * DAY_MS).toISOString().slice(0, 10);
}

// Number of days from `from` to `to`, both included ("2026-10-18".."2026-10-22" -> 5); 0 when to is before from
export function daysInRange(from: string, to: string): number {
  return Math.max(0, Math.round((Date.parse(to) - Date.parse(from)) / DAY_MS) + 1);
}

/*
  Gate-in / gate-out totals per date for the chart, every date of the range included (a quiet day is a 0 bar,
  not a missing one). The API only returns dates that had a move, one row per line operator.
*/
export function dailyTotals(
  from: string,
  to: string,
  rows: readonly DailyThroughput[],
): { date: string; gateIn: number; gateOut: number }[] {
  return Array.from({ length: daysInRange(from, to) }, (_, i) => {
    const date = addDays(from, i);
    const day = rows.filter((r) => r.date === date);
    return {
      date,
      gateIn: day.reduce((sum, r) => sum + r.gateIn, 0),
      gateOut: day.reduce((sum, r) => sum + r.gateOut, 0),
    };
  });
}

/*
  Status of a delivery order, computed by the API (feedback 6). Precedence: Cancelled, then Completed
  (every line fully released), then Expired, then NotStarted (nothing released) / InProgress.
*/
export type DeliveryOrderStatus =
  'NotStarted' | 'InProgress' | 'Completed' | 'Expired' | 'Cancelled';

export const DELIVERY_ORDER_STATUSES: DeliveryOrderStatus[] = [
  'NotStarted',
  'InProgress',
  'Completed',
  'Expired',
  'Cancelled',
];

export const DELIVERY_ORDER_STATUS_LABEL: Record<DeliveryOrderStatus, string> = {
  NotStarted: 'Chưa giao',
  InProgress: 'Đang giao',
  Completed: 'Đã giao đủ',
  Expired: 'Hết hạn',
  Cancelled: 'Đã huỷ',
};

// "Dry 20ft"; a line without size (created before Q1) covers both sizes
export function orderLineLabel(line: Pick<DeliveryOrderLine, 'category' | 'size'>): string {
  return `${CATEGORY_LABEL[line.category]} ${line.size ? SIZE_LABEL[line.size] : '20/40ft'}`;
}

// "Dry 20ft 0/2 · Reefer 40ft 1/1" = released / ordered per line, as in the order list
export function releasedSummary(order: Pick<DeliveryOrder, 'lines'>): string {
  return order.lines
    .map((l) => `${orderLineLabel(l)} ${l.releasedQuantity}/${l.quantity}`)
    .join(' · ');
}

// Q7: an order can be cancelled only while nothing has been released under it
export function canCancelOrder(order: Pick<DeliveryOrder, 'status' | 'lines'>): boolean {
  return order.status !== 'Cancelled' && order.lines.every((l) => l.releasedQuantity === 0);
}

// ---------- inspection & repair (feedback 3, 4, 8; Q4, Q6) ----------

/*
  Q6 flow: Chờ giám định -> (Đạt | Chờ duyệt phương án) -> Đã duyệt sửa -> Đang sửa -> Chờ nghiệm thu
  -> (Đạt | back to Đang sửa). Only Passed (Đạt) closes the case and lets the container leave.
*/
export type RepairCaseStatus =
  | 'AwaitingInspection'
  | 'AwaitingEstimateApproval'
  | 'RepairApproved'
  | 'InRepair'
  | 'AwaitingAcceptance'
  | 'Passed';

export type RepairAction =
  | 'InspectPassed'
  | 'InspectNeedsRepair'
  | 'ApproveRepair'
  | 'StartRepair'
  | 'CompleteRepair'
  | 'AcceptPassed'
  | 'AcceptRejected';

// Where a damage was seen: gate-in, the yard / move screen, the container catalog, or an inspection
export type RepairFindingSource = 'GateIn' | 'Yard' | 'Catalog' | 'Inspection';

// External* events record the outside repair; they do not change the case status
export type RepairEventKind =
  | 'Opened'
  | 'FindingAdded'
  | RepairAction
  | 'ExternalRequested'
  | 'ExternalCancelled'
  | 'SentForExternalRepair'
  | 'ReturnedFromExternalRepair'
  // Repair costs (feedback 4); EstimateApproved is the step that approves the repair
  | 'EstimateCreated'
  | 'EstimateSubmitted'
  | 'EstimateApproved'
  | 'EstimateRejected'
  | 'EstimateChangesRequested'
  | 'PayerChanged';

export const REPAIR_STATUS_LABEL: Record<RepairCaseStatus, string> = {
  AwaitingInspection: 'Chờ giám định',
  AwaitingEstimateApproval: 'Chờ duyệt phương án',
  RepairApproved: 'Đã duyệt sửa',
  InRepair: 'Đang sửa',
  AwaitingAcceptance: 'Chờ nghiệm thu',
  Passed: 'Đạt',
};

// Statuses in flow order, for the status filter
export const REPAIR_STATUSES: RepairCaseStatus[] = [
  'AwaitingInspection',
  'AwaitingEstimateApproval',
  'RepairApproved',
  'InRepair',
  'AwaitingAcceptance',
  'Passed',
];

// Button text of each action on the detail panel
export const REPAIR_ACTION_LABEL: Record<RepairAction, string> = {
  InspectPassed: 'Giám định: Đạt, không cần sửa',
  InspectNeedsRepair: 'Giám định: Cần sửa',
  ApproveRepair: 'Duyệt phương án sửa',
  StartRepair: 'Bắt đầu sửa',
  CompleteRepair: 'Sửa xong, chờ nghiệm thu',
  AcceptPassed: 'Nghiệm thu: Đạt',
  AcceptRejected: 'Nghiệm thu: Sửa lại',
};

// History line text; an action event reads as what was done
export const REPAIR_EVENT_LABEL: Record<RepairEventKind, string> = {
  Opened: 'Mở hồ sơ',
  FindingAdded: 'Ghi nhận thêm hư hỏng',
  InspectPassed: 'Giám định đạt, không cần sửa',
  InspectNeedsRepair: 'Giám định: cần sửa',
  ApproveRepair: 'Duyệt phương án sửa',
  StartRepair: 'Bắt đầu sửa',
  CompleteRepair: 'Sửa xong',
  AcceptPassed: 'Nghiệm thu đạt',
  AcceptRejected: 'Nghiệm thu không đạt, sửa lại',
  ExternalRequested: 'Yêu cầu chuyển sửa ngoài',
  ExternalCancelled: 'Huỷ yêu cầu sửa ngoài',
  SentForExternalRepair: 'Xuất bãi đi sửa ngoài',
  ReturnedFromExternalRepair: 'Về bãi sau sửa ngoài',
  EstimateCreated: 'Lập dự toán',
  EstimateSubmitted: 'Gửi duyệt dự toán',
  EstimateApproved: 'Dự toán được duyệt',
  EstimateRejected: 'Dự toán bị từ chối',
  EstimateChangesRequested: 'Yêu cầu chỉnh sửa dự toán',
  PayerChanged: 'Cập nhật bên chịu phí',
};

export const FINDING_SOURCE_LABEL: Record<RepairFindingSource, string> = {
  GateIn: 'Nhập bãi',
  Yard: 'Trong bãi',
  Catalog: 'Danh mục',
  Inspection: 'Giám định',
};

// Actions that need a note (the API answers 400 RepairCase.ReasonRequired without one)
export const REPAIR_ACTIONS_NEEDING_NOTE: readonly RepairAction[] = ['AcceptRejected'];

// One row of GET /api/repair-cases; blockCode..tier = where the container is now (null outside the yard)
export interface RepairCaseSummary {
  id: string;
  containerId: string;
  containerNumber: string;
  size: ContainerSize;
  category: ContainerCategory;
  status: RepairCaseStatus;
  operatorCode: string | null;
  openedAt: string;
  closedAt: string | null;
  latestFinding: string;
  findingCount: number;
  photoCount: number;
  blockCode: string | null;
  bay: number | null;
  row: number | null;
  tier: number | null;
  // The outside repair in progress (Requested or GatedOut), if any
  externalStatus: ExternalRepairStatus | null;
  externalFacility: string | null;
  // Latest estimate version (feedback 4), null when the case has none
  estimateVersion: number | null;
  estimateStatus: RepairEstimateStatus | null;
  estimateTotal: number | null;
  estimateCurrency: string | null;
}

export interface RepairFinding {
  id: string;
  description: string;
  source: RepairFindingSource;
  visitId: string | null;
  reportedAt: string;
  reportedBy: string | null;
}

export interface RepairPhoto {
  id: string;
  findingId: string | null;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  uploadedAt: string;
  uploadedBy: string | null;
}

export interface RepairCaseEvent {
  id: string;
  kind: RepairEventKind;
  fromStatus: RepairCaseStatus | null;
  toStatus: RepairCaseStatus;
  note: string | null;
  at: string;
  by: string | null;
}

// GET /api/repair-cases/{id}; allowedActions = what the API accepts from the current status
export interface RepairCaseDetail {
  id: string;
  containerId: string;
  containerNumber: string;
  isoCode: string;
  size: ContainerSize;
  category: ContainerCategory;
  ownerCode: string | null;
  status: RepairCaseStatus;
  repairNeeded: boolean | null;
  openedAt: string;
  closedAt: string | null;
  visitId: string | null;
  operatorCode: string | null;
  conditionAtGateIn: ContainerCondition | null;
  inYard: boolean;
  blockCode: string | null;
  bay: number | null;
  row: number | null;
  tier: number | null;
  allowedActions: RepairAction[];
  findings: RepairFinding[];
  photos: RepairPhoto[];
  history: RepairCaseEvent[];
  // True when an outside repair can be requested now (case approved or in repair, in the yard, none active)
  canRequestExternal: boolean;
  transfers: ExternalRepair[];
  // Every estimate version, newest first; canCreateEstimate = a new version can be made now
  estimates: RepairEstimate[];
  canCreateEstimate: boolean;
}

// Filters of GET /api/repair-cases; open: true = not Passed yet, false = Passed only
export interface RepairCaseFilter {
  search?: string;
  status?: RepairCaseStatus | null;
  open?: boolean | null;
  operatorId?: string | null;
  containerId?: string | null;
  reportedFrom?: string | null;
  reportedTo?: string | null;
  // true = only cases with an outside repair in progress; facility = part of the facility name
  external?: boolean | null;
  facility?: string | null;
  // Latest estimate version has an item paid by this party / in this allocation state
  payer?: CostPayer | null;
  payerStatus?: CostPayerStatus | null;
}

// POST /api/repair-cases/report (source GateIn is refused: gate-in reports with the gate-in itself)
export interface ReportDamageRequest {
  containerId: string;
  description: string;
  source: Exclude<RepairFindingSource, 'GateIn'>;
}

// created = this report opened the case (false: it was added to the open case)
export interface RecordedDamage {
  caseId: string;
  created: boolean;
  findingId: string;
}

// Q4: JPG / PNG of at most 5 MB, checked before upload (the API checks the bytes again)
export const PHOTO_MAX_BYTES = 5 * 1024 * 1024;
export const PHOTO_TYPES: readonly string[] = ['image/jpeg', 'image/png'];

// null when the file can be uploaded, otherwise the reason in Vietnamese
export function photoFileError(file: Pick<File, 'type' | 'size' | 'name'>): string | null {
  if (!PHOTO_TYPES.includes(file.type)) return `${file.name}: chỉ nhận ảnh JPG hoặc PNG.`;
  if (file.size > PHOTO_MAX_BYTES) return `${file.name}: ảnh tối đa 5 MB.`;
  if (file.size === 0) return `${file.name}: file rỗng.`;
  return null;
}

// ---------- repair costs (feedback 4: chi phí sửa chữa) ----------

/*
  One estimate version per proposal: Draft (items editable) -> Submitted (locked, waiting for the approving
  party) -> Approved / Rejected / ChangesRequested. The first approved version approves the repair
  (Chờ duyệt phương án -> Đã duyệt sửa). A new version starts as a copy of the latest one.
*/
export type RepairEstimateStatus =
  'Draft' | 'Submitted' | 'Approved' | 'Rejected' | 'ChangesRequested';
export type RepairEstimateDecision = 'Approve' | 'Reject' | 'RequestChanges';
export type CostPayer = 'Undetermined' | 'LineOperator' | 'Owner' | 'Customer' | 'Depot' | 'Other';
// Expected = proposed; Agreed = the party accepted; Disputed = the party contests it (reason required)
export type CostPayerStatus = 'Expected' | 'Agreed' | 'Disputed';

export const ESTIMATE_STATUS_LABEL: Record<RepairEstimateStatus, string> = {
  Draft: 'Nháp',
  Submitted: 'Chờ duyệt',
  Approved: 'Đã duyệt',
  Rejected: 'Bị từ chối',
  ChangesRequested: 'Yêu cầu chỉnh sửa',
};

export const ESTIMATE_DECISION_LABEL: Record<RepairEstimateDecision, string> = {
  Approve: 'Duyệt',
  Reject: 'Từ chối',
  RequestChanges: 'Yêu cầu chỉnh sửa',
};

export const COST_PAYER_LABEL: Record<CostPayer, string> = {
  Undetermined: 'Chưa xác định',
  LineOperator: 'Hãng khai thác',
  Owner: 'Chủ sở hữu',
  Customer: 'Khách hàng',
  Depot: 'Depot',
  Other: 'Bên khác',
};

export const COST_PAYER_STATUS_LABEL: Record<CostPayerStatus, string> = {
  Expected: 'Dự kiến',
  Agreed: 'Đã thống nhất',
  Disputed: 'Tranh chấp',
};

export const ESTIMATE_DECISIONS: RepairEstimateDecision[] = ['Approve', 'RequestChanges', 'Reject'];
export const COST_PAYERS: CostPayer[] = [
  'LineOperator',
  'Owner',
  'Customer',
  'Depot',
  'Other',
  'Undetermined',
];
export const COST_PAYER_STATUSES: CostPayerStatus[] = ['Expected', 'Agreed', 'Disputed'];
// Same list as the API validator (RepairEstimateRules.Currencies)
export const ESTIMATE_CURRENCIES: readonly string[] = ['VND', 'USD'];
export const ESTIMATE_MAX_LINES = 50;
export const ESTIMATE_MAX_AMOUNT = 999_999_999_999;

export interface RepairEstimateLine {
  id: string;
  position: number;
  // The reported damage this item costs, when there is one
  findingId: string | null;
  damageDescription: string;
  repairMethod: string;
  amount: number;
  payer: CostPayer;
  payerName: string | null;
  payerStatus: CostPayerStatus;
  // Dispute reason or allocation result
  payerNote: string | null;
}

// Sum of the items of one version per paying party
export interface PayerTotal {
  payer: CostPayer;
  payerName: string | null;
  amount: number;
}

export interface RepairEstimate {
  id: string;
  version: number;
  status: RepairEstimateStatus;
  currency: string;
  total: number;
  note: string | null;
  evidence: string | null;
  createdAt: string;
  createdBy: string | null;
  submittedAt: string | null;
  submittedBy: string | null;
  decidedAt: string | null;
  decisionRecordedBy: string | null;
  // Who decided (party and person) and the e-mail / document it was given in
  approverParty: string | null;
  approverName: string | null;
  approvalReference: string | null;
  decisionNote: string | null;
  lines: RepairEstimateLine[];
  payerTotals: PayerTotal[];
}

// One item as the draft table sends it (PUT /api/repair-estimates/{id} replaces the whole table)
export interface RepairEstimateLineInput {
  findingId: string | null;
  damageDescription: string;
  repairMethod: string;
  amount: number;
  payer: CostPayer;
  payerName: string | null;
}

export interface UpdateEstimateRequest {
  currency: string;
  note: string | null;
  evidence: string | null;
  lines: RepairEstimateLineInput[];
}

// Approve needs approverParty; Reject / RequestChanges need the note (reason)
export interface EstimateDecisionRequest {
  decision: RepairEstimateDecision;
  approverParty: string | null;
  approverName: string | null;
  approvalReference: string | null;
  note: string | null;
}

// Disputed needs the note; Other needs payerName
export interface LinePayerRequest {
  payer: CostPayer;
  payerName: string | null;
  status: CostPayerStatus;
  note: string | null;
}

/*
  "1.500.000 VND" / "1.250,50 USD": Vietnamese grouping; VND has no decimals, other currencies keep cents.
*/
export function formatMoney(amount: number, currency: string): string {
  const digits = currency === 'VND' ? 0 : 2;
  const text = new Intl.NumberFormat('vi-VN', {
    minimumFractionDigits: 0,
    maximumFractionDigits: digits,
  }).format(amount);
  return `${text} ${currency}`;
}

// "Chủ sở hữu (Triton)" when the role is completed by a name
export function payerText(item: Pick<RepairEstimateLine, 'payer' | 'payerName'>): string {
  const role = COST_PAYER_LABEL[item.payer];
  return item.payerName ? `${role} (${item.payerName})` : role;
}

// ---------- outside repair (feedback 4: sửa ngoài) ----------

/*
  Requested on the repair page -> GatedOut (confirmed on the gate-out page, no delivery order)
  -> Returned (new gate-in); or Cancelled before it leaves. The repair case stays open throughout.
*/
export type ExternalRepairStatus = 'Requested' | 'Cancelled' | 'GatedOut' | 'Returned';

export const EXTERNAL_REPAIR_STATUS_LABEL: Record<ExternalRepairStatus, string> = {
  Requested: 'Chờ xuất đi sửa',
  Cancelled: 'Đã huỷ',
  GatedOut: 'Đang sửa ngoài',
  Returned: 'Đã về bãi',
};

// blockCode..tier = where the container is now (null when it is at the facility)
export interface ExternalRepair {
  id: string;
  repairCaseId: string;
  caseStatus: RepairCaseStatus;
  containerId: string;
  containerNumber: string;
  isoCode: string;
  size: ContainerSize;
  category: ContainerCategory;
  status: ExternalRepairStatus;
  facility: string;
  reason: string;
  reference: string;
  requestedAt: string;
  requestedBy: string | null;
  cancelledAt: string | null;
  cancelReason: string | null;
  gatedOutAt: string | null;
  returnedAt: string | null;
  blockCode: string | null;
  bay: number | null;
  row: number | null;
  tier: number | null;
}

// POST /api/repair-cases/{id}/external-repair; reference = the line operator's approval / work order number
export interface RequestExternalRepairRequest {
  facility: string;
  reason: string;
  reference: string;
}
