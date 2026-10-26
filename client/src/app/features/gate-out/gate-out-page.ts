import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { map, startWith } from 'rxjs';
import { ApiError, toApiError } from '../../core/api/api-error';
import { DeliveryOrderApi } from '../../core/depot/delivery-order-api';
import {
  CATEGORY_LABEL,
  CONDITION_LABEL,
  DailyGateOut,
  DeliveryOrder,
  ExternalRepair,
  GATE_OUT_BLOCK_LABEL,
  GATE_OUT_PURPOSE_LABEL,
  GateOutCandidate,
  REPAIR_STATUS_LABEL,
  SIZE_LABEL,
  formatDate,
  formatDateTime,
  formatPosition,
  gateOutBadge,
  gateOutResolution,
  orderLineLabel,
  situationText,
} from '../../core/depot/depot-models';
import { RepairApi } from '../../core/depot/repair-api';
import { YardApi } from '../../core/depot/yard-api';
import { SearchPageLoader, SearchSelect } from '../../shared/search-select/search-select';
import { ToastService } from '../../shared/toast/toast-service';

// One line of the "check before gate-out" panel (mockup 04); pending = not chosen yet
interface GateOutCheck {
  title: string;
  detail: string;
  state: 'ok' | 'fail' | 'pending';
}

// Cấp rỗng under a delivery order, or chuyển sửa ngoài (feedback 4 / 6)
export type GateOutTab = 'release' | 'repair';

// Rows asked per page by the search dropdowns (about 5 are visible, the rest load while scrolling)
const SEARCH_PAGE_SIZE = 20;

// The order line a container uses: same type, and same size (or a line without size, from before Q1)
export function lineFor(
  order: Pick<DeliveryOrder, 'lines'>,
  candidate: Pick<GateOutCandidate, 'category' | 'size'>,
) {
  return (
    order.lines.find(
      (l) => l.category === candidate.category && (l.size === null || l.size === candidate.size),
    ) ?? null
  );
}

// "Dry 20ft còn 2/3 · Reefer 40ft còn 1/1": what the order still needs
export function remainingSummary(order: Pick<DeliveryOrder, 'lines'>): string {
  return order.lines
    .filter((l) => l.remainingQuantity > 0)
    .map((l) => `${orderLineLabel(l)} còn ${l.remainingQuantity}/${l.quantity}`)
    .join(' · ');
}

/*
  Spec section 3 + feedback 6: gate-out starts from the delivery order. The clerk picks a usable order
  (Chưa giao / Đang giao, not expired), then only the containers that match it are offered: same line
  operator, a type + size the order still needs. Containers that cannot leave yet are listed after the
  others with the reason (open repair case, damaged, another container on top), so the clerk knows why.
  ?orderId= keeps the order in the URL (links from the order list, back from the repair page).

  Feedback 4: the second tab lets a container requested for outside repair leave without an order;
  the request itself is made on the repair page. The API checks every rule again.
*/
@Component({
  selector: 'app-gate-out-page',
  imports: [ReactiveFormsModule, DatePipe, RouterLink, SearchSelect],
  templateUrl: './gate-out-page.html',
  styleUrl: './gate-out-page.css',
})
export class GateOutPage {
  private readonly yard = inject(YardApi);
  private readonly orderApi = inject(DeliveryOrderApi);
  private readonly repairs = inject(RepairApi);
  private readonly toast = inject(ToastService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly fb = inject(FormBuilder).nonNullable;

  protected readonly sizeLabel = SIZE_LABEL;
  protected readonly categoryLabel = CATEGORY_LABEL;
  protected readonly conditionLabel = CONDITION_LABEL;
  protected readonly repairStatusLabel = REPAIR_STATUS_LABEL;
  protected readonly blockLabel = GATE_OUT_BLOCK_LABEL;
  protected readonly purposeLabel = GATE_OUT_PURPOSE_LABEL;
  protected readonly formatDate = formatDate;
  protected readonly formatDateTime = formatDateTime;
  protected readonly remainingSummary = remainingSummary;

  protected readonly tab = signal<GateOutTab>('release');

  // Cấp rỗng: order first, then a container of that order (GateOutCommand)
  protected readonly form = this.fb.group({
    order: new FormControl<DeliveryOrder | null>(null, Validators.required),
    container: new FormControl<GateOutCandidate | null>(null, Validators.required),
    vehicleNo: ['', [Validators.required, Validators.maxLength(20)]],
  });

  // Chuyển sửa ngoài: a transfer waiting to leave (GateOutForRepairCommand)
  protected readonly repairForm = this.fb.group({
    transfer: new FormControl<ExternalRepair | null>(null, Validators.required),
    vehicleNo: ['', [Validators.required, Validators.maxLength(20)]],
  });

  // Damage found while checking before release (ReportDamageCommand, source Yard)
  protected readonly damageForm = this.fb.group({
    description: ['', [Validators.required, Validators.maxLength(500)]],
  });

  // Search loaders: usable orders only; candidates of the picked order; transfers waiting to leave
  protected readonly loadOrders: SearchPageLoader<DeliveryOrder> = (term, page) =>
    this.orderApi.list(page, SEARCH_PAGE_SIZE, { search: term, usable: true });
  protected readonly loadCandidates: SearchPageLoader<GateOutCandidate> = (term, page) =>
    this.yard.gateOutCandidates(
      this.form.controls.order.value?.id ?? '',
      term,
      page,
      SEARCH_PAGE_SIZE,
    );
  protected readonly loadTransfers: SearchPageLoader<ExternalRepair> = (term, page) =>
    this.repairs.externalRepairs('Requested', term, page, SEARCH_PAGE_SIZE);

  protected readonly orderKey = (o: DeliveryOrder) => o.id;
  protected readonly orderLabel = (o: DeliveryOrder) => o.orderNumber;
  protected readonly orderDetail = (o: DeliveryOrder) =>
    `${o.lineOperatorCode} · ${o.customerName} · hạn ${formatDate(o.expiryDate)} · ${remainingSummary(o)}`;
  // Feedback 26/10: rows look like the gate-in dropdown (type line, chip, "where is it" line)
  protected readonly candidateKey = (c: GateOutCandidate) => c.containerId;
  protected readonly candidateLabel = (c: GateOutCandidate) => c.containerNumber;
  protected readonly candidateDetail = (c: GateOutCandidate) =>
    `${c.isoCode} · ${SIZE_LABEL[c.size]} · ${CATEGORY_LABEL[c.category]} · ${CONDITION_LABEL[c.condition]}`;
  protected readonly candidateBadge = gateOutBadge;
  protected readonly candidateNote = (c: GateOutCandidate) => situationText(c.situation);
  protected readonly transferKey = (t: ExternalRepair) => t.id;
  protected readonly transferLabel = (t: ExternalRepair) => t.containerNumber;
  protected readonly transferDetail = (t: ExternalRepair) =>
    `${t.facility} · ${t.reference} · ${t.blockCode ? formatPosition(t.blockCode, t.bay, t.row, t.tier) : 'không ở trong bãi'}`;

  protected readonly todayGateOuts = signal<DailyGateOut[]>([]);
  protected readonly orderError = signal<string | null>(null);
  protected readonly submitting = signal(false);
  protected readonly submitted = signal(false);
  protected readonly apiError = signal<ApiError | null>(null);
  protected readonly repairSubmitted = signal(false);
  protected readonly repairError = signal<ApiError | null>(null);
  protected readonly reporting = signal(false);
  protected readonly reportError = signal<ApiError | null>(null);

  // Bumped after each gate-out, so the container dropdown drops the container that just left
  private readonly releases = signal(0);
  private readonly repairExits = signal(0);

  // Form values as signals, so the checks follow what is picked
  private readonly value = toSignal(
    this.form.valueChanges.pipe(
      startWith(null),
      map(() => this.form.getRawValue()),
    ),
    { initialValue: this.form.getRawValue() },
  );
  private readonly repairValue = toSignal(
    this.repairForm.valueChanges.pipe(
      startWith(null),
      map(() => this.repairForm.getRawValue()),
    ),
    { initialValue: this.repairForm.getRawValue() },
  );

  protected readonly order = computed(() => this.value().order);
  protected readonly candidate = computed(() => this.value().container);
  protected readonly transfer = computed(() => this.repairValue().transfer);

  // A new order (or a release) drops the picked container and the old results
  protected readonly candidateResetKey = computed(
    () => `${this.order()?.id ?? ''}|${this.releases()}`,
  );
  protected readonly transferResetKey = computed(() => this.repairExits());

  // Same order of rules as GateOutCommandHandler, with what was found for the picked container
  protected readonly checks = computed<GateOutCheck[]>(() => {
    const order = this.order();
    const candidate = this.candidate();
    if (!order) return [];

    const line = candidate ? lineFor(order, candidate) : null;
    return [
      {
        title: 'Lệnh còn dùng được',
        detail: `${order.orderNumber} của ${order.lineOperatorCode} · hạn ${formatDate(order.expiryDate)} (giờ Việt Nam)`,
        state: 'ok',
      },
      candidate
        ? this.inYardCheck(candidate)
        : {
            title: 'Container của lệnh, đang ở trong bãi',
            detail: 'Chọn container',
            state: 'pending',
          },
      candidate
        ? this.aboveCheck(candidate)
        : { title: 'Không bị container khác đè lên', detail: 'Chọn container', state: 'pending' },
      candidate
        ? this.repairCheck(candidate)
        : { title: 'Không có hồ sơ sửa chữa đang mở', detail: 'Chọn container', state: 'pending' },
      candidate
        ? this.lineCheck(candidate, line)
        : {
            title: 'Lệnh còn số lượng cho loại container',
            detail: 'Chọn container',
            state: 'pending',
          },
    ];
  });

  // Feedback 26/10: link to the screen that makes the picked container available (null = it can leave)
  protected readonly resolution = computed(() => {
    const order = this.order();
    const candidate = this.candidate();
    return order && candidate ? gateOutResolution(candidate, order.id) : null;
  });

  protected readonly canGateOut = computed(() => {
    const checks = this.checks();
    return checks.length > 0 && checks.every((c) => c.state === 'ok');
  });

  constructor() {
    this.loadToday();

    // The URL decides the tab and the order (links from the order list and the repair page, reload)
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe((params) => {
      this.tab.set(params.get('tab') === 'repair' ? 'repair' : 'release');
      const orderId = params.get('orderId');
      if (orderId && orderId !== this.order()?.id) this.openOrder(orderId);
    });

    // Picking another order keeps it in the URL; the damage form belongs to the picked container
    this.form.controls.order.valueChanges.pipe(takeUntilDestroyed()).subscribe((order) => {
      this.orderError.set(null);
      this.apiError.set(null);
      this.setQuery({ orderId: order?.id ?? null });
    });
    this.form.controls.container.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => {
      this.apiError.set(null);
      this.reportError.set(null);
      this.damageForm.reset();
    });
  }

  protected showTab(tab: GateOutTab): void {
    this.setQuery({ tab: tab === 'repair' ? 'repair' : null });
  }

  protected submit(): void {
    this.submitted.set(true);
    this.apiError.set(null);
    this.form.markAllAsTouched();

    const order = this.order();
    const candidate = this.candidate();
    if (!order || !candidate || this.form.invalid || !this.canGateOut()) return;

    this.submitting.set(true);
    this.yard
      .gateOut({
        containerId: candidate.containerId,
        deliveryOrderId: order.id,
        vehicleNo: this.form.controls.vehicleNo.value.trim(),
      })
      .subscribe({
        next: (result) => {
          this.toast.success(
            `Đã xuất ${result.containerNumber} theo lệnh ${result.orderNumber} · ` +
              `${CATEGORY_LABEL[result.category]} ${SIZE_LABEL[result.size]} còn ${result.remainingQuantity}`,
          );
          this.submitting.set(false);
          this.afterRelease(order.id);
        },
        error: (err: unknown) => {
          this.apiError.set(toApiError(err));
          this.submitting.set(false);
        },
      });
  }

  // Chuyển sửa ngoài: the container leaves for the facility, its case stays open
  protected submitRepair(): void {
    this.repairSubmitted.set(true);
    this.repairError.set(null);
    this.repairForm.markAllAsTouched();

    const transfer = this.transfer();
    if (!transfer || this.repairForm.invalid) return;

    this.submitting.set(true);
    this.yard
      .gateOutForRepair({
        transferId: transfer.id,
        vehicleNo: this.repairForm.controls.vehicleNo.value.trim(),
      })
      .subscribe({
        next: (result) => {
          this.toast.success(`Đã xuất ${result.containerNumber} đi sửa tại ${result.facility}`);
          this.submitting.set(false);
          this.repairForm.reset();
          this.repairSubmitted.set(false);
          this.repairExits.update((n) => n + 1);
          this.loadToday();
        },
        error: (err: unknown) => {
          this.repairError.set(toApiError(err));
          this.submitting.set(false);
        },
      });
  }

  /*
    A damage seen while checking the container before release: it opens a repair case (or joins the
    open one), which blocks this gate-out until the case passes on the repair page.
  */
  protected reportDamage(): void {
    const candidate = this.candidate();
    this.damageForm.markAllAsTouched();
    if (!candidate || this.damageForm.invalid || this.reporting()) return;

    this.reporting.set(true);
    this.reportError.set(null);
    this.repairs
      .report({
        containerId: candidate.containerId,
        description: this.damageForm.controls.description.value.trim(),
        source: 'Yard',
      })
      .subscribe({
        next: (recorded) => {
          this.toast.success(
            `Đã ghi nhận hư hỏng ${candidate.containerNumber} vào hồ sơ giám định`,
          );
          this.reporting.set(false);
          // The picked row now has an open case: show it blocked, as the API would answer
          this.form.controls.container.setValue({
            ...candidate,
            condition: 'Damaged',
            blockReason: 'RepairCase',
            repairCaseId: recorded.caseId,
            repairStatus: candidate.repairStatus ?? 'AwaitingInspection',
            eligible: false,
          });
        },
        error: (err: unknown) => {
          this.reportError.set(toApiError(err));
          this.reporting.set(false);
        },
      });
  }

  // "Làm lại": clear the container and the vehicle for the next truck; the order stays (it often has several)
  protected resetForNext(): void {
    this.form.controls.container.setValue(null);
    this.form.controls.vehicleNo.reset();
    this.submitted.set(false);
    this.apiError.set(null);
    this.reportError.set(null);
    this.damageForm.reset();
  }

  protected invalid(name: 'order' | 'container' | 'vehicleNo'): boolean {
    const control = this.form.controls[name];
    return control.invalid && (control.touched || this.submitted());
  }

  protected repairInvalid(name: 'transfer' | 'vehicleNo'): boolean {
    const control = this.repairForm.controls[name];
    return control.invalid && (control.touched || this.repairSubmitted());
  }

  protected positionText(position: {
    blockCode: string | null;
    bay: number | null;
    row: number | null;
    tier: number | null;
  }): string {
    return position.blockCode
      ? formatPosition(position.blockCode, position.bay, position.row, position.tier)
      : '—';
  }

  // "Dry 20ft 0/1" = what is left on the order line / what was ordered; the facility for an outside repair
  protected remainingText(row: DailyGateOut): string {
    if (row.exitPurpose === 'ExternalRepair') return row.repairFacility ?? '—';
    return row.orderLineQuantity === null
      ? '—'
      : `${CATEGORY_LABEL[row.category]} ${SIZE_LABEL[row.size]} ${row.orderLineRemaining}/${row.orderLineQuantity}`;
  }

  /*
    Feedback 26/10: the dropdown also lists the operator's containers that are not in the yard;
    the check says where the container is instead (outside facility, last exit, never came in).
  */
  private inYardCheck(candidate: GateOutCandidate): GateOutCheck {
    const title = 'Container của lệnh, đang ở trong bãi';
    if (candidate.blockReason === 'NotInYard') {
      return {
        title,
        detail: `${situationText(candidate.situation) || 'Không ở trong bãi'}. Chỉ xuất được container đang trong bãi.`,
        state: 'fail',
      };
    }
    const since = candidate.gateInAt ? ` · vào ${formatDateTime(candidate.gateInAt)}` : '';
    return {
      title,
      detail: `${SIZE_LABEL[candidate.size]} ${CATEGORY_LABEL[candidate.category]} · ${this.positionText(candidate)}${since}`,
      state: 'ok',
    };
  }

  // The type + size must be on the order with something left; a container of another type is listed, not released
  private lineCheck(
    candidate: GateOutCandidate,
    line: DeliveryOrder['lines'][number] | null,
  ): GateOutCheck {
    if (!line) {
      return {
        title: 'Lệnh có loại container này',
        detail: `Lệnh không có dòng ${CATEGORY_LABEL[candidate.category]} ${SIZE_LABEL[candidate.size]}. Chọn container khác.`,
        state: 'fail',
      };
    }
    return {
      title: `Lệnh còn số lượng cho ${orderLineLabel(line)}`,
      detail:
        line.remainingQuantity > 0
          ? `Còn ${line.remainingQuantity}/${line.quantity}, sau khi xuất còn ${line.remainingQuantity - 1}`
          : `Đã giao đủ ${line.quantity}/${line.quantity}. Chọn container loại khác.`,
      state: line.remainingQuantity > 0 ? 'ok' : 'fail',
    };
  }

  private aboveCheck(candidate: GateOutCandidate): GateOutCheck {
    const title = 'Không bị container khác đè lên';
    if (candidate.blockReason === 'NotInYard')
      return { title, detail: 'Không ở trong bãi', state: 'pending' };
    if (candidate.blockReason === 'ContainerAbove') {
      return {
        title,
        detail: `${candidate.aboveContainerNumber} đang xếp phía trên. Đảo chuyển container đó trước.`,
        state: 'fail',
      };
    }
    if (candidate.blockReason === 'RepairCase') {
      // The API does not look further for a container that cannot leave anyway
      return { title, detail: 'Kiểm tra lại sau khi hồ sơ sửa chữa đạt', state: 'pending' };
    }
    return {
      title,
      detail: candidate.tier === null ? 'Block ảo, không xếp chồng' : 'Phía trên trống',
      state: 'ok',
    };
  }

  // Feedback 4: an open repair case blocks release whatever its step; "Đạt" closes it
  private repairCheck(candidate: GateOutCandidate): GateOutCheck {
    const title = 'Không có hồ sơ sửa chữa đang mở';
    // Out of the yard the case is not checked here (it shows in the dropdown line); the API checks again
    if (candidate.blockReason === 'NotInYard') {
      const step = candidate.situation?.openRepairStatus;
      return step
        ? { title, detail: `Hồ sơ sửa chữa ở bước "${REPAIR_STATUS_LABEL[step]}"`, state: 'fail' }
        : { title, detail: 'Không ở trong bãi', state: 'pending' };
    }
    if (candidate.blockReason === 'RepairCase') {
      const step = candidate.repairStatus
        ? `ở bước "${REPAIR_STATUS_LABEL[candidate.repairStatus]}"`
        : 'chưa đạt';
      return {
        title,
        detail: `Hồ sơ giám định/sửa chữa ${step}. Chỉ xuất được khi hồ sơ đạt.`,
        state: 'fail',
      };
    }
    if (candidate.blockReason === 'Damaged') {
      return {
        title,
        detail: 'Container đang ghi Hư hỏng. Tải lại trang để xem hồ sơ.',
        state: 'fail',
      };
    }
    return { title, detail: 'Không có hư hỏng chờ xử lý', state: 'ok' };
  }

  // The order from the URL; a cancelled, expired or completed order is not offered for gate-out
  private openOrder(id: string): void {
    this.orderApi.get(id).subscribe({
      next: (order) => {
        if (order.status === 'NotStarted' || order.status === 'InProgress') {
          this.form.controls.order.setValue(order);
        } else {
          this.form.controls.order.setValue(null);
          this.orderError.set(`Lệnh ${order.orderNumber} không còn dùng để xuất bãi được.`);
        }
      },
      error: (err: unknown) => this.orderError.set(toApiError(err).message),
    });
  }

  // Reloads the order (quantities changed); it stays picked while it still has something to release
  private afterRelease(orderId: string): void {
    this.resetForNext();
    this.releases.update((n) => n + 1);
    this.loadToday();
    this.orderApi.get(orderId).subscribe({
      next: (order) => {
        const usable = order.status === 'NotStarted' || order.status === 'InProgress';
        this.form.controls.order.setValue(usable ? order : null);
        if (!usable) this.toast.success(`Lệnh ${order.orderNumber} đã giao đủ`);
      },
      error: () => this.form.controls.order.setValue(null),
    });
  }

  private setQuery(params: Record<string, string | null>): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: params,
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  private loadToday(): void {
    this.yard.daily().subscribe({
      next: (daily) => this.todayGateOuts.set(daily.gateOuts),
      error: () => this.todayGateOuts.set([]),
    });
  }
}
