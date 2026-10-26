-- Depot statistics in plain SQL (PostgreSQL), same rules as the statistics API.
-- Run in pgAdmin (Aspire dashboard -> pgadmin -> database TechSpherex-db -> Query Tool).
-- All dates are Vietnam dates: timestamps are converted with AT TIME ZONE 'Asia/Ho_Chi_Minh'
-- before taking the date, so 00:00-07:00 Vietnam time is not counted as the previous day.
-- Tables and relations: see docs/erd/depot-erd.dbml.


-- 1. Containers in the yard now, per line operator and dwell time (screen Tồn bãi)
--    Dwell days = Vietnam calendar days since gate-in (0 on the gate-in day).
--    Short stay: 0-9 days; long stay: 10 days or more.
--    In the yard = open visit (GateOutAt IS NULL), virtual blocks included. A container at an
--    outside repair facility has no open visit, so it is not counted.
--    LEFT JOIN keeps operating lines with no container in the yard (all zeros); an owner-only
--    partner (leasing company, IsOperator = false) is listed only when it has containers.
WITH in_yard AS (
    SELECT "LineOperatorId",
           (now() AT TIME ZONE 'Asia/Ho_Chi_Minh')::date
             - ("GateInAt" AT TIME ZONE 'Asia/Ho_Chi_Minh')::date AS dwell_days
    FROM "ContainerVisits"
    WHERE "GateOutAt" IS NULL                                       -- still in the yard
)
SELECT
    lo."Code"                                             AS line_operator,
    lo."Name"                                             AS name,
    COUNT(y.dwell_days) FILTER (WHERE y.dwell_days < 10)  AS short_stay_0_9_days,
    COUNT(y.dwell_days) FILTER (WHERE y.dwell_days >= 10) AS long_stay_10_days_plus,
    COUNT(y.dwell_days)                                   AS total
FROM "LineOperators" lo
LEFT JOIN in_yard y ON y."LineOperatorId" = lo."Id"
GROUP BY lo."Code", lo."Name", lo."IsOperator"
HAVING lo."IsOperator" OR COUNT(y.dwell_days) > 0
ORDER BY lo."Code";


-- 2. Gate-in and gate-out volume per line operator per day (screen Sản lượng)
--    Period: the last 30 Vietnam days up to today (both ends included). For a fixed period,
--    replace the BETWEEN bounds, e.g. BETWEEN DATE '2026-10-01' AND DATE '2026-10-31'.
--    A gate-in counts on its gate-in day, a gate-out on its gate-out day: a container that came in
--    before the period and left inside it counts as a gate-out only.
--    A gate-out counts for the line operator of its visit, split by exit purpose:
--    EmptyRelease = cấp rỗng under a delivery order, ExternalRepair = chuyển sửa ngoài.
WITH gate_events AS (
    SELECT "LineOperatorId",
           ("GateInAt" AT TIME ZONE 'Asia/Ho_Chi_Minh')::date  AS day,
           1 AS gate_in,
           0 AS empty_release,
           0 AS external_repair
    FROM "ContainerVisits"
    UNION ALL
    SELECT "LineOperatorId",
           ("GateOutAt" AT TIME ZONE 'Asia/Ho_Chi_Minh')::date AS day,
           0,
           CASE WHEN "ExitPurpose" = 'EmptyRelease'   THEN 1 ELSE 0 END,
           CASE WHEN "ExitPurpose" = 'ExternalRepair' THEN 1 ELSE 0 END
    FROM "ContainerVisits"
    WHERE "GateOutAt" IS NOT NULL
)
SELECT
    e.day,
    lo."Code"                                    AS line_operator,
    SUM(e.gate_in)                               AS gate_in,
    SUM(e.empty_release + e.external_repair)     AS gate_out,
    SUM(e.empty_release)                         AS gate_out_empty_release,
    SUM(e.external_repair)                       AS gate_out_external_repair
FROM gate_events e
JOIN "LineOperators" lo ON lo."Id" = e."LineOperatorId"
WHERE e.day BETWEEN (now() AT TIME ZONE 'Asia/Ho_Chi_Minh')::date - 29
                AND (now() AT TIME ZONE 'Asia/Ho_Chi_Minh')::date
GROUP BY e.day, lo."Code"
ORDER BY e.day, lo."Code";


-- 3. Containers in the yard with their dwell days, longest first, ranked per line operator
--    (window function: RANK() restarts for each line operator). Bay/Row/Tier are empty in a virtual block.
SELECT
    lo."Code"                                                        AS line_operator,
    c."ContainerNumber"                                              AS container_number,
    c."Size"                                                         AS size,
    c."Category"                                                     AS category,
    c."Condition"                                                    AS condition,
    b."Code"                                                         AS block,
    v."Bay", v."Row", v."Tier",
    (now() AT TIME ZONE 'Asia/Ho_Chi_Minh')::date
      - (v."GateInAt" AT TIME ZONE 'Asia/Ho_Chi_Minh')::date        AS dwell_days,
    RANK() OVER (PARTITION BY lo."Code" ORDER BY v."GateInAt")       AS rank_in_line_operator
FROM "ContainerVisits" v
JOIN "Containers"    c  ON c."Id"  = v."ContainerId"
JOIN "LineOperators" lo ON lo."Id" = v."LineOperatorId"
JOIN "Blocks"        b  ON b."Id"  = v."BlockId"
WHERE v."GateOutAt" IS NULL
ORDER BY lo."Code", rank_in_line_operator;


-- 4. History of one container: one row per visit, newest first (screen Lịch sử container)
--    Change the container number (MSCU8000343 = demo scenario BackFromOutsideRepair, 2 visits). The line operator is the one of each visit, not today's.
--    Dwell days of an open visit run to today; a closed one stops at its gate-out.
SELECT
    c."ContainerNumber"                                              AS container_number,
    v."GateInAt"  AT TIME ZONE 'Asia/Ho_Chi_Minh'                    AS gate_in_vn,
    v."GateOutAt" AT TIME ZONE 'Asia/Ho_Chi_Minh'                    AS gate_out_vn,
    lo."Code"                                                        AS line_operator,
    ow."Code"                                                        AS owner,
    v."ExitPurpose"                                                  AS exit_purpose,
    o."OrderNumber"                                                  AS delivery_order,
    COALESCE((v."GateOutAt" AT TIME ZONE 'Asia/Ho_Chi_Minh')::date,
             (now() AT TIME ZONE 'Asia/Ho_Chi_Minh')::date)
      - (v."GateInAt" AT TIME ZONE 'Asia/Ho_Chi_Minh')::date        AS dwell_days,
    (SELECT COUNT(*) FROM "ContainerMoves" m WHERE m."VisitId" = v."Id") AS moves
FROM "ContainerVisits" v
JOIN "Containers"     c  ON c."Id"  = v."ContainerId"
JOIN "LineOperators"  lo ON lo."Id" = v."LineOperatorId"
LEFT JOIN "LineOperators"  ow ON ow."Id" = v."OwnerId"
LEFT JOIN "DeliveryOrders" o  ON o."Id"  = v."DeliveryOrderId"
WHERE c."ContainerNumber" = 'MSCU8000343'
ORDER BY v."GateInAt" DESC;
