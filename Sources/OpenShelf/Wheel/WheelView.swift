import SwiftUI

struct WheelView: View {
    let model: WheelModel
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    /// Width of the glass rim visible around the petals.
    private let rimWidth: CGFloat = 9

    var body: some View {
        let slots: [WheelSlot] = model.slots
        let geometry = WheelGeometry(wedgeCount: slots.count)
        let badges: [Int: String] = model.showsBadges ? WheelBadges.keys(for: slots) : [:]
        let discDiameter: CGFloat = (geometry.outerRadius + rimWidth) * 2
        let hubDiameter: CGFloat = (geometry.innerRadius - 6) * 2
        let pageAnimation: Animation = reduceMotion ? .easeOut(duration: 0.15) : .easeInOut(duration: 0.18)

        ZStack {
            GlassDisc()
                .frame(width: discDiameter, height: discDiameter)

            ZStack {
                ForEach(Array(slots.enumerated()), id: \.offset) { index, slot in
                    PetalView(
                        slot: slot,
                        geometry: geometry,
                        index: index,
                        page: model.page,
                        pageCount: model.pageCount,
                        isHighlighted: model.highlightedSlot == index,
                        isDwelling: model.dwellingSlot == index,
                        badge: badges[index]
                    )
                    // Petals bloom outward one after another.
                    .scaleEffect(model.isPresented || reduceMotion ? 1 : 0.6)
                    .opacity(model.isPresented ? 1 : 0)
                    .animation(
                        reduceMotion ? .easeOut(duration: 0.12) : .spring(response: 0.3, dampingFraction: 0.74).delay(Double(index) * 0.012),
                        value: model.isPresented
                    )
                }
            }
            // A different wheel gets fresh petals rather than morphing the old ones.
            .id(model.itemsID)

            HubView(systemImage: model.highlighted?.systemImage, isActive: model.highlighted != nil)
                .frame(width: hubDiameter, height: hubDiameter)

            // The dragged file's thumbnail sits over the hub, so the label
            // floats above the rim where nothing covers it.
            LabelPill(text: model.hint ?? pillTitle, isActive: model.highlighted != nil && model.hint == nil)
                .offset(y: -(geometry.outerRadius + rimWidth + 20))
        }
        .frame(width: WheelGeometry.panelSide, height: WheelGeometry.panelSide)
        .scaleEffect(model.isPresented || reduceMotion ? 1 : 0.55)
        .opacity(model.isPresented ? 1 : 0)
        .animation(reduceMotion ? .easeOut(duration: 0.12) : .spring(response: 0.28, dampingFraction: 0.78), value: model.isPresented)
        .animation(.spring(response: 0.24, dampingFraction: 0.72), value: model.highlightedSlot)
        // Turning the page swaps the petals' contents in place; the wheel stays put.
        .animation(pageAnimation, value: model.page)
    }

    /// Names the wheel too when it was opened from the keyboard.
    private var pillTitle: String {
        model.wheelName.map { "\($0) · \(currentLabel)" } ?? currentLabel
    }

    /// The hovered wedge, or the files' size and, on a paged wheel, the page.
    private var currentLabel: String {
        if let label = model.highlighted?.label { return label }
        let summary: String = model.dragSummary.isEmpty ? "Drop" : model.dragSummary
        guard model.pageCount > 1 else { return summary }
        return "\(summary) · \(model.page + 1) of \(model.pageCount)"
    }
}

/// The current label above the wheel: file size, the hovered action, or a hint.
private struct LabelPill: View {
    let text: String
    let isActive: Bool

    var body: some View {
        Text(text)
            .font(.system(size: 12, weight: .semibold))
            .lineLimit(1)
            .contentTransition(.interpolate)
            .foregroundStyle(isActive ? Color.white : Color.primary)
            .padding(.horizontal, 12)
            .frame(height: 26)
            .background {
                Capsule()
                    .fill(isActive ? AnyShapeStyle(Color.accentColor) : AnyShapeStyle(.regularMaterial))
                    .overlay(Capsule().strokeBorder(.white.opacity(0.4), lineWidth: 0.8))
                    .shadow(color: .black.opacity(0.18), radius: 6, y: 2)
            }
            .fixedSize()
            .accessibilityHidden(true)
    }
}

/// Translucent disc the petals sit on, with a bright rim like the edge of glass.
private struct GlassDisc: View {
    var body: some View {
        Circle()
            .fill(.ultraThinMaterial)
            .overlay {
                Circle().fill(
                    LinearGradient(colors: [.white.opacity(0.22), .white.opacity(0.04)], startPoint: .top, endPoint: .bottom)
                )
            }
            .overlay {
                Circle().strokeBorder(
                    LinearGradient(colors: [.white.opacity(0.85), .white.opacity(0.15), .white.opacity(0.45)], startPoint: .top, endPoint: .bottom),
                    lineWidth: 1.2
                )
            }
            .shadow(color: .black.opacity(0.18), radius: 22, y: 10)
            .shadow(color: .black.opacity(0.08), radius: 2, y: 1)
    }
}


private struct PetalView: View {
    let slot: WheelSlot
    let geometry: WheelGeometry
    let index: Int
    let page: Int
    let pageCount: Int
    let isHighlighted: Bool
    let isDwelling: Bool
    /// The number key shown while ⌘ is held on a keyboard wheel.
    var badge: String?
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let center: CGFloat = geometry.centerAngle(ofWedge: index)
        let shape = RoundedAnnularSector(
            startAngle: center - geometry.sweep / 2,
            endAngle: center + geometry.sweep / 2,
            innerRadius: geometry.innerRadius,
            outerRadius: geometry.outerRadius,
            gap: 7,
            cornerRadius: 16
        )
        let isCrowded: Bool = geometry.wedgeCount > 8
        // Narrow wedges are wider further out, so their labels sit a little outward.
        let labelRadius: CGFloat = (geometry.innerRadius + geometry.outerRadius) / 2 + (isCrowded ? 9 : 3)
        let labelX: CGFloat = sin(center) * labelRadius
        let labelY: CGFloat = -cos(center) * labelRadius

        ZStack {
            ZStack {
                shape.fill(fill)
                shape.fill(
                    LinearGradient(colors: [.white.opacity(isHighlighted ? 0.28 : 0.35), .clear], startPoint: .top, endPoint: .center)
                )
                .opacity(colorScheme == .dark && !isHighlighted ? 0.25 : 1)
                shape.stroke(.white.opacity(isHighlighted ? 0.55 : 0.4), lineWidth: 0.8)
            }
            // A placeholder on a paged wheel's last page: there, but faint.
            .opacity(slot.isEmpty ? 0.3 : 1)

            PetalLabel(slot: slot, isCrowded: isCrowded, page: page, pageCount: pageCount, isDwelling: isDwelling)
                .frame(width: geometry.labelWidth(atRadius: labelRadius))
                .foregroundStyle(isHighlighted ? Color.white : Color.primary.opacity(0.85))
                .offset(x: labelX, y: labelY)
                // Fill the wheel so a turn swings the label around its centre.
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .id(slot)
                .transition(pageTurnTransition)

            if let badge {
                // Near the wedge's outer, clockwise corner.
                let angle: CGFloat = center + geometry.sweep * 0.3
                let radius: CGFloat = geometry.outerRadius - 13
                Text(badge)
                    .font(.system(size: 10, weight: .bold).monospacedDigit())
                    .foregroundStyle(.white)
                    .frame(width: 16, height: 16)
                    .background(Circle().fill(Color.black.opacity(0.62)))
                    .offset(x: sin(angle) * radius, y: -cos(angle) * radius)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .transition(.opacity)
            }
        }
        .shadow(color: isHighlighted ? Color.accentColor.opacity(0.45) : .clear, radius: 10, y: 3)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(badge.map { "\(slot.accessibilityLabel), Command \($0)" } ?? slot.accessibilityLabel)
        .accessibilityAddTraits(isHighlighted ? .isSelected : [])
        .accessibilityHidden(slot.isEmpty)
    }

    /// The outgoing contents fade out turning a little clockwise as the new
    /// ones fade in after them; just a fade with Reduce Motion.
    private var pageTurnTransition: AnyTransition {
        guard !reduceMotion else { return .opacity }
        return .asymmetric(
            insertion: .opacity.combined(with: .modifier(active: Turned(degrees: -10), identity: Turned(degrees: 0))),
            removal: .opacity.combined(with: .modifier(active: Turned(degrees: 10), identity: Turned(degrees: 0)))
        )
    }

    private var fill: AnyShapeStyle {
        if isHighlighted {
            return AnyShapeStyle(
                LinearGradient(colors: [Color.accentColor.opacity(0.95), Color.accentColor.opacity(0.78)], startPoint: .top, endPoint: .bottom)
            )
        }
        return AnyShapeStyle(Color.white.opacity(colorScheme == .dark ? 0.12 : 0.5))
    }
}

private struct Turned: ViewModifier {
    let degrees: Double

    func body(content: Content) -> some View {
        content.rotationEffect(.degrees(degrees))
    }
}

/// What a petal shows: an action's symbol and name, a format's name, or for
/// More and Back a symbol in a ring that fills while the pointer rests there,
/// over dots for the pages.
private struct PetalLabel: View {
    let slot: WheelSlot
    let isCrowded: Bool
    let page: Int
    let pageCount: Int
    let isDwelling: Bool

    var body: some View {
        switch slot {
        case .item(let item):
            VStack(spacing: 4) {
                if let systemImage = item.systemImage {
                    Image(systemName: systemImage)
                        .font(.system(size: isCrowded ? 15 : 17, weight: .semibold))
                        .symbolRenderingMode(.hierarchical)
                }
                Text(item.title.uppercased())
                    // Format names stand alone, so they get a larger label.
                    .font(.system(size: item.systemImage == nil ? 13 : 9.5, weight: .bold))
                    .tracking(0.4)
                    .lineLimit(1)
                    .minimumScaleFactor(0.65)
            }
        case .more, .back:
            VStack(spacing: 6) {
                ZStack {
                    Circle()
                        .stroke(lineWidth: 2)
                        .opacity(0.25)
                    Circle()
                        .trim(from: 0, to: isDwelling ? 1 : 0)
                        .stroke(style: StrokeStyle(lineWidth: 2, lineCap: .round))
                        .rotationEffect(.degrees(-90))
                        .animation(isDwelling ? .linear(duration: PageTurnDwell.delaySeconds) : .easeOut(duration: 0.12), value: isDwelling)
                    if let systemImage = slot.systemImage {
                        Image(systemName: systemImage)
                            .font(.system(size: 13, weight: .bold))
                    }
                }
                .frame(width: 30, height: 30)
                PageDots(page: page, count: pageCount)
            }
        case .empty:
            EmptyView()
        }
    }
}

/// One dot per page, the current one solid.
private struct PageDots: View {
    let page: Int
    let count: Int

    var body: some View {
        HStack(spacing: 4) {
            ForEach(0..<count, id: \.self) { index in
                Circle()
                    .frame(width: 5, height: 5)
                    .opacity(index == page ? 1 : 0.35)
            }
        }
    }
}

/// The centre of the wheel: a glass ring that lights up with the chosen
/// wedge's symbol. Words live in the label above the rim, which the dragged
/// file's thumbnail can't cover.
private struct HubView: View {
    let systemImage: String?
    let isActive: Bool

    var body: some View {
        ZStack {
            Circle()
                .fill(isActive ? AnyShapeStyle(Color.accentColor.opacity(0.9)) : AnyShapeStyle(.white.opacity(0.08)))
                .overlay(Circle().strokeBorder(.white.opacity(0.5), lineWidth: 0.8))
            if let systemImage {
                Image(systemName: systemImage)
                    .font(.system(size: 22, weight: .semibold))
                    .foregroundStyle(.white)
                    .transition(.scale(scale: 0.6).combined(with: .opacity))
                    .id(systemImage)
            }
        }
        .accessibilityHidden(true)
    }
}

/// A ring segment with rounded corners and straight sides that leave a gap of
/// constant width to its neighbours. Angles are in radians, clockwise from 12 o'clock.
struct RoundedAnnularSector: Shape {
    var startAngle: CGFloat
    var endAngle: CGFloat
    var innerRadius: CGFloat
    var outerRadius: CGFloat
    var gap: CGFloat
    var cornerRadius: CGFloat

    func path(in rect: CGRect) -> Path {
        let center = CGPoint(x: rect.midX, y: rect.midY)
        let halfGap = gap / 2
        let radius = min(cornerRadius, (outerRadius - innerRadius) / 2)

        func point(_ distance: CGFloat, _ angle: CGFloat) -> CGPoint {
            CGPoint(x: center.x + distance * sin(angle), y: center.y - distance * cos(angle))
        }
        // Angle of each straight side at a given distance, offset so the gap stays parallel.
        func startSide(_ distance: CGFloat) -> CGFloat { startAngle + asin(min(halfGap / distance, 1)) }
        func endSide(_ distance: CGFloat) -> CGFloat { endAngle - asin(min(halfGap / distance, 1)) }
        // SwiftUI arc angles start at 3 o'clock and grow clockwise on screen.
        func arcAngle(_ angle: CGFloat) -> Angle { .radians(Double(angle) - .pi / 2) }

        let outerSpan = endSide(outerRadius) - startSide(outerRadius)
        let innerSpan = endSide(innerRadius) - startSide(innerRadius)
        let outerInset = min(radius / outerRadius, outerSpan / 2)
        let innerInset = min(radius / innerRadius, innerSpan / 2)

        var path = Path()
        path.move(to: point(outerRadius - radius, startSide(outerRadius - radius)))
        path.addQuadCurve(
            to: point(outerRadius, startSide(outerRadius) + outerInset),
            control: point(outerRadius, startSide(outerRadius))
        )
        path.addArc(
            center: center, radius: outerRadius,
            startAngle: arcAngle(startSide(outerRadius) + outerInset),
            endAngle: arcAngle(endSide(outerRadius) - outerInset),
            clockwise: false
        )
        path.addQuadCurve(
            to: point(outerRadius - radius, endSide(outerRadius - radius)),
            control: point(outerRadius, endSide(outerRadius))
        )
        path.addLine(to: point(innerRadius + radius, endSide(innerRadius + radius)))
        path.addQuadCurve(
            to: point(innerRadius, endSide(innerRadius) - innerInset),
            control: point(innerRadius, endSide(innerRadius))
        )
        path.addArc(
            center: center, radius: innerRadius,
            startAngle: arcAngle(endSide(innerRadius) - innerInset),
            endAngle: arcAngle(startSide(innerRadius) + innerInset),
            clockwise: true
        )
        path.addQuadCurve(
            to: point(innerRadius + radius, startSide(innerRadius + radius)),
            control: point(innerRadius, startSide(innerRadius))
        )
        path.closeSubpath()
        return path
    }
}
