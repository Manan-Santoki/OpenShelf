import SwiftUI

struct WheelView: View {
    let model: WheelModel
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    /// Width of the glass rim visible around the petals.
    private let rimWidth: CGFloat = 9

    var body: some View {
        let geometry = WheelGeometry(wedgeCount: model.items.count)
        let discDiameter = (geometry.outerRadius + rimWidth) * 2

        ZStack {
            GlassDisc()
                .frame(width: discDiameter, height: discDiameter)

            ForEach(Array(model.items.enumerated()), id: \.element.id) { index, item in
                PetalView(
                    item: item,
                    geometry: geometry,
                    index: index,
                    isHighlighted: model.highlighted == item
                )
                // Petals bloom outward one after another.
                .scaleEffect(model.isPresented || reduceMotion ? 1 : 0.6)
                .opacity(model.isPresented ? 1 : 0)
                .animation(
                    reduceMotion ? .easeOut(duration: 0.12) : .spring(response: 0.3, dampingFraction: 0.74).delay(Double(index) * 0.012),
                    value: model.isPresented
                )
            }

            HubView(highlighted: model.highlighted)
                .frame(width: (geometry.innerRadius - 6) * 2, height: (geometry.innerRadius - 6) * 2)

            // The dragged file's thumbnail sits over the hub, so the label
            // floats above the rim where nothing covers it.
            LabelPill(text: model.hint ?? pillTitle, isActive: model.highlighted != nil && model.hint == nil)
                .offset(y: -(geometry.outerRadius + rimWidth + 20))
        }
        .frame(width: WheelGeometry.panelSide, height: WheelGeometry.panelSide)
        .scaleEffect(model.isPresented || reduceMotion ? 1 : 0.55)
        .opacity(model.isPresented ? 1 : 0)
        .animation(reduceMotion ? .easeOut(duration: 0.12) : .spring(response: 0.28, dampingFraction: 0.78), value: model.isPresented)
        .animation(.spring(response: 0.24, dampingFraction: 0.72), value: model.highlighted)
    }

    /// Names the wheel too when it was opened from the keyboard.
    private var pillTitle: String {
        model.wheelName.map { "\($0) · \(currentLabel)" } ?? currentLabel
    }

    private var currentLabel: String {
        model.highlighted?.title ?? (model.dragSummary.isEmpty ? "Drop" : model.dragSummary)
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
    let item: WheelItem
    let geometry: WheelGeometry
    let index: Int
    let isHighlighted: Bool
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        let center = geometry.centerAngle(ofWedge: index)
        let shape = RoundedAnnularSector(
            startAngle: center - geometry.sweep / 2,
            endAngle: center + geometry.sweep / 2,
            innerRadius: geometry.innerRadius,
            outerRadius: geometry.outerRadius,
            gap: 7,
            cornerRadius: 16
        )
        let isCrowded = geometry.wedgeCount > 8
        // Narrow wedges are wider further out, so their labels sit a little outward.
        let labelRadius = (geometry.innerRadius + geometry.outerRadius) / 2 + (isCrowded ? 9 : 3)

        ZStack {
            shape.fill(fill)
            shape.fill(
                LinearGradient(colors: [.white.opacity(isHighlighted ? 0.28 : 0.35), .clear], startPoint: .top, endPoint: .center)
            )
            .opacity(colorScheme == .dark && !isHighlighted ? 0.25 : 1)
            shape.stroke(.white.opacity(isHighlighted ? 0.55 : 0.4), lineWidth: 0.8)

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
            .frame(width: geometry.labelWidth(atRadius: labelRadius))
            .foregroundStyle(isHighlighted ? Color.white : Color.primary.opacity(0.85))
            .offset(x: sin(center) * labelRadius, y: -cos(center) * labelRadius)
        }
        .shadow(color: isHighlighted ? Color.accentColor.opacity(0.45) : .clear, radius: 10, y: 3)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(isHighlighted ? .isSelected : [])
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

/// The centre of the wheel: a glass ring that lights up with the chosen
/// wedge's symbol. Words live in the label above the rim, which the dragged
/// file's thumbnail can't cover.
private struct HubView: View {
    let highlighted: WheelItem?

    var body: some View {
        ZStack {
            Circle()
                .fill(highlighted == nil ? AnyShapeStyle(.white.opacity(0.08)) : AnyShapeStyle(Color.accentColor.opacity(0.9)))
                .overlay(Circle().strokeBorder(.white.opacity(0.5), lineWidth: 0.8))
            if let systemImage = highlighted?.systemImage {
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
